// Package telemetry pushes metrics, states and events from a Go application
// into NetCrunch.
//
// Instrumentation only mutates memory. A separate flush snapshots the registry
// and sends absolute current values, so nothing in a request path touches the
// network, and one request carries every value — which matters because the
// receiver caps pending payloads per sensor and discards the overflow without
// reporting it.
//
// See spec/v1.md for the wire format and spec/client-model.md for the
// behaviour above it.
package telemetry

import (
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"net/url"
	"strings"
	"sync"
	"time"
)

// Options configures a Telemetry.
type Options struct {
	// Endpoint is the URL from the Telemetry sensor form. Treat it as a secret:
	// it is never written to an error or a log by this package.
	Endpoint string

	// Token is the bearer token from the Telemetry sensor, sent as an
	// Authorization header. Optional because a sensor need not have one
	// configured, and because servers before NetCrunch 16.0 do not check it;
	// see spec/v1.md section 1.1.
	Token string

	// FlushInterval starts a background flush loop when greater than zero.
	// Close stops it. Zero means flush only when asked.
	FlushInterval time.Duration

	// Retain must exceed FlushInterval, or values expire between sends.
	// Defaults to 5 minutes.
	Retain time.Duration

	// Remove is how long an object survives with no data. Defaults to 24 hours.
	Remove time.Duration

	// Timeout for a single request. Defaults to 30 seconds.
	Timeout time.Duration

	// MaxRetries for transport failures and 5xx responses. Defaults to 3.
	MaxRetries int

	// OnError receives failures from background flushes, which have nowhere
	// else to go. Ignored for explicit Flush calls, which return the error.
	OnError func(error)

	// HTTPClient defaults to a client with no timeout of its own; per-request
	// timeouts come from Timeout.
	HTTPClient *http.Client
}

type statusEntry struct {
	Value    string `json:"value"`
	Message  string `json:"message,omitempty"`
	Critical bool   `json:"critical,omitempty"`
	Data     any    `json:"data,omitempty"`
}

type eventEntry struct {
	Message  string `json:"message"`
	Severity string `json:"severity,omitempty"`
}

type counterPath struct {
	Object   string `json:"object"`
	Counter  string `json:"counter"`
	Instance string `json:"instance,omitempty"`
}

type counterEntry struct {
	Path  counterPath `json:"path"`
	Value float64     `json:"value"`
}

type stampEntry struct {
	object      string
	counter     string
	statusKey   string
	observedAt  time.Time
	statusValue string
}

type payload struct {
	Retain   int                    `json:"retain"`
	Remove   int                    `json:"remove"`
	Counters []counterEntry         `json:"counters,omitempty"`
	Statuses map[string]statusEntry `json:"statuses,omitempty"`
	Events   []eventEntry           `json:"events,omitempty"`
	Data     map[string]any         `json:"data,omitempty"`
}

// Telemetry stages values in memory and flushes them as a single payload.
// It is safe for concurrent use.
type Telemetry struct {
	endpoint   string
	token      string
	retain     time.Duration
	remove     time.Duration
	timeout    time.Duration
	maxRetries int
	onError    func(error)
	client     *http.Client

	mu           sync.Mutex
	counters     map[string]*Counter
	counterOrder []*Counter
	statuses     map[string]statusEntry
	timestamps   map[string]stampEntry
	dataObjects  map[string]map[string]any
	events       []eventEntry

	flushMu sync.Mutex

	stopOnce sync.Once
	stop     chan struct{}
	done     chan struct{}
}

// New validates the options and returns a Telemetry. If FlushInterval is
// greater than zero, a background flush loop starts; Close stops it.
func New(options Options) (*Telemetry, error) {
	if strings.TrimSpace(options.Endpoint) == "" {
		return nil, fmt.Errorf("endpoint is required — copy it from the Telemetry sensor form")
	}
	parsed, err := url.Parse(options.Endpoint)
	if err != nil || !parsed.IsAbs() || (parsed.Scheme != "http" && parsed.Scheme != "https") {
		return nil, fmt.Errorf("endpoint must be an absolute http or https URL")
	}

	retain := options.Retain
	if retain == 0 {
		retain = 5 * time.Minute
	}
	remove := options.Remove
	if remove == 0 {
		remove = 24 * time.Hour
	}
	timeout := options.Timeout
	if timeout == 0 {
		timeout = 30 * time.Second
	}
	maxRetries := options.MaxRetries
	if maxRetries == 0 {
		maxRetries = 3
	}
	client := options.HTTPClient
	if client == nil {
		client = &http.Client{}
	}

	if options.FlushInterval > 0 && retain <= options.FlushInterval {
		return nil, fmt.Errorf(
			"Retain (%s) must exceed FlushInterval (%s), or values expire between sends",
			retain, options.FlushInterval,
		)
	}

	t := &Telemetry{
		endpoint:    options.Endpoint,
		token:       options.Token,
		retain:      retain,
		remove:      remove,
		timeout:     timeout,
		maxRetries:  maxRetries,
		onError:     options.OnError,
		client:      client,
		counters:    make(map[string]*Counter),
		statuses:    make(map[string]statusEntry),
		timestamps:  make(map[string]stampEntry),
		dataObjects: make(map[string]map[string]any),
		stop:        make(chan struct{}),
		done:        make(chan struct{}),
	}

	if options.FlushInterval > 0 {
		go t.loop(options.FlushInterval)
	} else {
		close(t.done)
	}
	return t, nil
}

func (t *Telemetry) loop(interval time.Duration) {
	defer close(t.done)
	ticker := time.NewTicker(interval)
	defer ticker.Stop()
	for {
		select {
		case <-t.stop:
			return
		case <-ticker.C:
			if err := t.Flush(context.Background()); err != nil && t.onError != nil {
				t.onError(err)
			}
		}
	}
}

// -- staging ---------------------------------------------------------------

// Counter resolves a counter handle. The same object, counter and instance
// always return the same handle, so separate parts of a program instrumenting
// the same thing converge on one value. Pass "" for instance when there is none.
func (t *Telemetry) Counter(object, counter, instance string) (*Counter, error) {
	if err := validateCounterPath(object, counter); err != nil {
		return nil, err
	}

	key := object + "\x00" + counter + "\x00" + instance

	t.mu.Lock()
	defer t.mu.Unlock()
	if existing, ok := t.counters[key]; ok {
		return existing, nil
	}
	created := &Counter{object: object, counter: counter, instance: instance}
	t.counters[key] = created
	t.counterOrder = append(t.counterOrder, created)
	return created, nil
}

// MustCounter is Counter but panics on an invalid path, for package-level
// declarations where a bad name is a programming error rather than a condition
// to handle.
func (t *Telemetry) MustCounter(object, counter, instance string) *Counter {
	resolved, err := t.Counter(object, counter, instance)
	if err != nil {
		panic(err)
	}
	return resolved
}

// StatusOptions carries the optional parts of a status.
type StatusOptions struct {
	Message  string
	Critical bool
	Data     any
}

// Status stages a state with an optional explanation. Statuses are what
// NetCrunch alerting acts on — a counter on its own raises nothing.
func (t *Telemetry) Status(key, value string, options ...StatusOptions) error {
	if err := validateStatusKey(key); err != nil {
		return err
	}
	if err := validateStatusValue(value); err != nil {
		return err
	}

	entry := statusEntry{Value: value}
	if len(options) > 0 {
		entry.Message = options[0].Message
		entry.Critical = options[0].Critical
		// Guarded rather than assigned straight through: a nil map boxed in an
		// interface is not a nil interface, so `omitempty` would not fire and the
		// payload would carry "data": null for a status that has none.
		if !isNil(options[0].Data) {
			entry.Data = options[0].Data
		}
	}

	t.mu.Lock()
	defer t.mu.Unlock()
	t.statuses[key] = entry
	return nil
}

// Event stages a discrete occurrence. Events accumulate and are cleared once
// sent. Use a status for a condition that begins and later ends.
func (t *Telemetry) Event(message string, severity ...string) error {
	if err := validateEventMessage(message); err != nil {
		return err
	}
	entry := eventEntry{Message: message}
	if len(severity) > 0 {
		entry.Severity = severity[0]
	}

	t.mu.Lock()
	defer t.mu.Unlock()
	t.events = append(t.events, entry)
	return nil
}

// Timestamp records when something last happened.
//
// The wire format has no timestamp type, and a raw clock value means nothing
// outside the process that produced it. So this becomes two things: an age in
// seconds, which an alert threshold can be set on, and a status message carrying
// the absolute time, for a person to read. The age is computed at flush time.
func (t *Telemetry) Timestamp(object, counter, statusKey string, observedAt time.Time) error {
	if err := validateCounterPath(object, counter); err != nil {
		return err
	}
	if err := validateStatusKey(statusKey); err != nil {
		return err
	}

	t.mu.Lock()
	defer t.mu.Unlock()
	t.timestamps[statusKey] = stampEntry{
		object:      object,
		counter:     counter,
		statusKey:   statusKey,
		observedAt:  observedAt,
		statusValue: "OK",
	}
	return nil
}

// -- data objects ----------------------------------------------------------

// DataObject is the generic form of a data object, behind Table, TimeSeries and
// CategoryChart. Members holds the type-specific arrays.
type DataObject struct {
	Type       string
	Name       string
	SeriesName string
	Message    string
	Status     string
	Members    map[string]any
}

// Data stages a data object rendered on the sensor's page.
//
// The id is the object's identity across payloads: staging the same id again
// replaces it. There is no incremental form — a data object is a whole view
// each time.
//
// A data object's Status is part of what is displayed. Alerting acts on
// statuses; a red table is not an alert.
func (t *Telemetry) Data(id string, object DataObject) error {
	if err := validateDataObject(id, object.Type, object.Members); err != nil {
		return err
	}

	encoded := map[string]any{"type": object.Type}
	for _, member := range dataTypeMembers[object.Type] {
		encoded[member] = object.Members[member]
	}
	if object.Name != "" {
		encoded["name"] = object.Name
	}
	// seriesName labels a plotted series; a table has no series to label.
	if object.SeriesName != "" && object.Type != "table" {
		encoded["seriesName"] = object.SeriesName
	}
	if object.Message != "" {
		encoded["message"] = object.Message
	}
	if object.Status != "" {
		encoded["status"] = object.Status
	}

	t.mu.Lock()
	defer t.mu.Unlock()
	t.dataObjects[id] = encoded
	return nil
}

// Table stages a table. Every row must have as many cells as there are columns.
type Table struct {
	Name    string
	Message string
	Status  string
	Columns []any
	Rows    [][]any
}

// Table stages a table data object.
func (t *Telemetry) Table(id string, table Table) error {
	rows := make([]any, len(table.Rows))
	for i, row := range table.Rows {
		rows[i] = row
	}
	return t.Data(id, DataObject{
		Type:    "table",
		Name:    table.Name,
		Message: table.Message,
		Status:  table.Status,
		Members: map[string]any{"columns": table.Columns, "rows": rows},
	})
}

// TimeSeries stages a time chart. Timestamps are epoch milliseconds and must be
// the same length as Values.
type TimeSeries struct {
	Name       string
	SeriesName string
	Message    string
	Status     string
	Timestamps []int64
	Values     []float64
}

// TimeSeries stages a time-series data object.
func (t *Telemetry) TimeSeries(id string, series TimeSeries) error {
	return t.Data(id, DataObject{
		Type:       "time-series",
		Name:       series.Name,
		SeriesName: series.SeriesName,
		Message:    series.Message,
		Status:     series.Status,
		Members:    map[string]any{"timestamps": series.Timestamps, "values": series.Values},
	})
}

// CategoryChart stages a labelled bar chart. Categories and Values must be the
// same length.
type CategoryChart struct {
	Name       string
	SeriesName string
	Message    string
	Status     string
	Categories []string
	Values     []float64
}

// CategoryChart stages a category data object. It is named apart from Category,
// which is the lifetime-bound aggregate — same word in NetCrunch, unrelated
// meanings.
func (t *Telemetry) CategoryChart(id string, chart CategoryChart) error {
	return t.Data(id, DataObject{
		Type:       "category",
		Name:       chart.Name,
		SeriesName: chart.SeriesName,
		Message:    chart.Message,
		Status:     chart.Status,
		Members:    map[string]any{"categories": chart.Categories, "values": chart.Values},
	})
}

// -- lifetime-bound aggregates ---------------------------------------------

// SelfCount holds one against a counter until Close.
//
//	lease := stats.SelfCount("Pool", "Leases Active", "")
//	defer lease.Close()
func (t *Telemetry) SelfCount(object, counter, instance string) *SelfCount {
	handle := t.MustCounter(object, counter, instance)
	handle.Inc()
	return &SelfCount{counter: handle}
}

// PartCount contributes a movable amount, withdrawn in full on Close.
func (t *Telemetry) PartCount(object, counter, instance string) *PartCount {
	return &PartCount{counter: t.MustCounter(object, counter, instance)}
}

// Category holds one against a single instance at a time, moving it as the value
// changes. For the chart of the same name, see CategoryChart.
func (t *Telemetry) Category(object, counter string) *CategoryCount {
	return &CategoryCount{
		resolve: func(instance string) *Counter {
			return t.MustCounter(object, counter, instance)
		},
	}
}

// -- payload ---------------------------------------------------------------

// BuildPayload returns the JSON a flush would post, without sending it. Members
// with nothing in them are omitted rather than sent empty.
func (t *Telemetry) BuildPayload(snapshotAt time.Time) ([]byte, error) {
	return json.Marshal(t.snapshot(snapshotAt))
}

func (t *Telemetry) snapshot(snapshotAt time.Time) payload {
	t.mu.Lock()
	defer t.mu.Unlock()

	built := payload{
		Retain: int(t.retain.Minutes()),
		Remove: int(t.remove.Minutes()),
	}

	for _, handle := range t.counterOrder {
		built.Counters = append(built.Counters, counterEntry{
			Path: counterPath{
				Object:   handle.object,
				Counter:  handle.counter,
				Instance: handle.instance,
			},
			Value: handle.Value(),
		})
	}

	if len(t.statuses) > 0 || len(t.timestamps) > 0 {
		built.Statuses = make(map[string]statusEntry, len(t.statuses)+len(t.timestamps))
		for key, entry := range t.statuses {
			built.Statuses[key] = entry
		}
	}

	// A timestamp contributes to both collections, so it is expanded here rather
	// than at the call site — the age is only meaningful against this snapshot.
	for _, stamp := range t.timestamps {
		built.Counters = append(built.Counters, counterEntry{
			Path:  counterPath{Object: stamp.object, Counter: stamp.counter},
			Value: float64(int64(snapshotAt.Sub(stamp.observedAt).Round(time.Second) / time.Second)),
		})
		built.Statuses[stamp.statusKey] = statusEntry{
			Value:   stamp.statusValue,
			Message: stamp.observedAt.UTC().Format("2006-01-02T15:04:05Z"),
		}
	}

	if len(t.events) > 0 {
		built.Events = append([]eventEntry(nil), t.events...)
	}
	if len(t.dataObjects) > 0 {
		built.Data = make(map[string]any, len(t.dataObjects))
		for id, object := range t.dataObjects {
			built.Data[id] = object
		}
	}
	return built
}

// -- sending ---------------------------------------------------------------

// Flush posts everything staged as a single request.
//
// Concurrent calls serialise rather than run together; each sends the absolute
// state at the moment it runs. Events are cleared on success. Counters and
// statuses are kept, so a long-running process keeps reporting current values
// without restating them.
func (t *Telemetry) Flush(ctx context.Context) error {
	t.flushMu.Lock()
	defer t.flushMu.Unlock()

	built := t.snapshot(time.Now())
	if built.Counters == nil && built.Statuses == nil && built.Events == nil && built.Data == nil {
		return nil
	}

	sentEvents := len(built.Events)

	body, err := json.Marshal(built)
	if err != nil {
		return fmt.Errorf("netcrunch telemetry payload could not be encoded: %w", err)
	}
	if err := t.post(ctx, body); err != nil {
		return err
	}

	// Trimmed rather than emptied: events staged while the request was in flight
	// have not been sent, and dropping them would lose them silently.
	t.mu.Lock()
	t.events = append([]eventEntry(nil), t.events[sentEvents:]...)
	t.mu.Unlock()
	return nil
}

// Clear discards everything staged.
func (t *Telemetry) Clear() {
	t.mu.Lock()
	defer t.mu.Unlock()
	t.counters = make(map[string]*Counter)
	t.counterOrder = nil
	t.statuses = make(map[string]statusEntry)
	t.timestamps = make(map[string]stampEntry)
	t.dataObjects = make(map[string]map[string]any)
	t.events = nil
}

// Close stops the background flush loop, if one is running, and flushes once
// more. Safe to call more than once.
func (t *Telemetry) Close(ctx context.Context) error {
	t.stopOnce.Do(func() { close(t.stop) })
	<-t.done
	return t.Flush(ctx)
}
