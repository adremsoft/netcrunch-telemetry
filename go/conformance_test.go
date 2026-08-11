package telemetry

import (
	"encoding/json"
	"os"
	"path/filepath"
	"reflect"
	"sort"
	"testing"
	"time"
)

// Runs the shared conformance suite. Fixtures live in ../conformance/cases and
// are shared with every other implementation, so "compatible with the spec"
// means the same thing in each.

// Placeholder only — never a real installation's endpoint. See CONTRIBUTING.md.
const testEndpoint = "https://netcrunch.example/api/rest/1/sensors/example@1/update"

type caseOptions struct {
	RetainMinutes int    `json:"retainMinutes"`
	RemoveMinutes int    `json:"removeMinutes"`
	SnapshotAt    string `json:"snapshotAt"`
}

type caseSnapshot struct {
	Counters []struct {
		Object   string  `json:"object"`
		Counter  string  `json:"counter"`
		Instance string  `json:"instance"`
		Value    float64 `json:"value"`
	} `json:"counters"`
	Statuses []struct {
		Key      string         `json:"key"`
		Value    string         `json:"value"`
		Message  string         `json:"message"`
		Critical bool           `json:"critical"`
		Data     map[string]any `json:"data"`
	} `json:"statuses"`
	Events []struct {
		Message  string `json:"message"`
		Severity string `json:"severity"`
	} `json:"events"`
	Data       []map[string]any `json:"data"`
	Timestamps []struct {
		Object     string `json:"object"`
		Counter    string `json:"counter"`
		StatusKey  string `json:"statusKey"`
		ObservedAt string `json:"observedAt"`
	} `json:"timestamps"`
}

type caseReject struct {
	Kind   string         `json:"kind"`
	Input  map[string]any `json:"input"`
	Reason string         `json:"reason"`
}

type caseFile struct {
	Name       string           `json:"name"`
	Options    caseOptions      `json:"options"`
	Snapshot   *caseSnapshot    `json:"snapshot"`
	Operations []map[string]any `json:"operations"`
	Rejects    []caseReject     `json:"rejects"`
	Expect     map[string]any   `json:"expect"`
}

func loadCases(t *testing.T) []caseFile {
	t.Helper()
	paths, err := filepath.Glob(filepath.Join("..", "conformance", "cases", "*.json"))
	if err != nil || len(paths) == 0 {
		t.Fatalf("no conformance cases found: %v", err)
	}
	sort.Strings(paths)

	cases := make([]caseFile, 0, len(paths))
	for _, path := range paths {
		raw, err := os.ReadFile(path)
		if err != nil {
			t.Fatalf("reading %s: %v", path, err)
		}
		var parsed caseFile
		if err := json.Unmarshal(raw, &parsed); err != nil {
			t.Fatalf("parsing %s: %v", path, err)
		}
		cases = append(cases, parsed)
	}
	return cases
}

func connect(t *testing.T, options caseOptions) *Telemetry {
	t.Helper()
	retain := 5 * time.Minute
	if options.RetainMinutes > 0 {
		retain = time.Duration(options.RetainMinutes) * time.Minute
	}
	remove := 24 * time.Hour
	if options.RemoveMinutes > 0 {
		remove = time.Duration(options.RemoveMinutes) * time.Minute
	}
	stats, err := New(Options{Endpoint: testEndpoint, Retain: retain, Remove: remove})
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	return stats
}

func text(source map[string]any, key string) string {
	value, _ := source[key].(string)
	return value
}

// dataObjectFrom converts a fixture record into the generic form. The type is
// carried through untouched, so an unknown-type rejection fails for the reason
// the fixture states rather than incidentally.
func dataObjectFrom(input map[string]any) (string, DataObject) {
	members := map[string]any{}
	for _, member := range []string{"columns", "rows", "timestamps", "values", "categories"} {
		if value, ok := input[member]; ok {
			members[member] = value
		}
	}
	return text(input, "id"), DataObject{
		Type:       text(input, "type"),
		Name:       text(input, "name"),
		SeriesName: text(input, "seriesName"),
		Message:    text(input, "message"),
		Status:     text(input, "status"),
		Members:    members,
	}
}

func applySnapshot(t *testing.T, stats *Telemetry, snapshot *caseSnapshot) {
	t.Helper()
	if snapshot == nil {
		return
	}

	for _, entry := range snapshot.Counters {
		handle, err := stats.Counter(entry.Object, entry.Counter, entry.Instance)
		if err != nil {
			t.Fatalf("Counter: %v", err)
		}
		handle.Set(entry.Value)
	}
	for _, entry := range snapshot.Statuses {
		err := stats.Status(entry.Key, entry.Value, StatusOptions{
			Message:  entry.Message,
			Critical: entry.Critical,
			Data:     entry.Data,
		})
		if err != nil {
			t.Fatalf("Status: %v", err)
		}
	}
	for _, entry := range snapshot.Events {
		if err := stats.Event(entry.Message, entry.Severity); err != nil {
			t.Fatalf("Event: %v", err)
		}
	}
	for _, entry := range snapshot.Data {
		id, object := dataObjectFrom(entry)
		if err := stats.Data(id, object); err != nil {
			t.Fatalf("Data: %v", err)
		}
	}
	for _, entry := range snapshot.Timestamps {
		observedAt, err := time.Parse(time.RFC3339, entry.ObservedAt)
		if err != nil {
			t.Fatalf("parsing observedAt: %v", err)
		}
		if err := stats.Timestamp(entry.Object, entry.Counter, entry.StatusKey, observedAt); err != nil {
			t.Fatalf("Timestamp: %v", err)
		}
	}
}

// closer is what all three aggregates have in common.
type closer interface{ Close() }

func runOperations(t *testing.T, stats *Telemetry, operations []map[string]any) {
	t.Helper()
	aggregates := map[string]any{}

	lookup := func(op map[string]any) any {
		id := text(op, "id")
		aggregate, ok := aggregates[id]
		if !ok {
			t.Fatalf("no aggregate bound to id %q", id)
		}
		return aggregate
	}

	for _, op := range operations {
		switch op["op"].(string) {
		case "counter":
			handle := stats.MustCounter(text(op, "object"), text(op, "counter"), text(op, "instance"))
			if value, ok := op["set"].(float64); ok {
				handle.Set(value)
			}
		case "selfCount":
			aggregates[text(op, "id")] = stats.SelfCount(text(op, "object"), text(op, "counter"), text(op, "instance"))
		case "partCount":
			aggregates[text(op, "id")] = stats.PartCount(text(op, "object"), text(op, "counter"), text(op, "instance"))
		case "category":
			aggregates[text(op, "id")] = stats.Category(text(op, "object"), text(op, "counter"))
		case "set":
			switch aggregate := lookup(op).(type) {
			case *PartCount:
				if err := aggregate.Set(op["value"].(float64)); err != nil {
					t.Fatalf("PartCount.Set: %v", err)
				}
			case *CategoryCount:
				// null clears the held instance; "" is how Go spells that.
				instance, _ := op["value"].(string)
				aggregate.Set(instance)
			default:
				t.Fatalf("set is not defined for %T", aggregate)
			}
		case "dispose":
			lookup(op).(closer).Close()
		case "assert":
			handle := stats.MustCounter(text(op, "object"), text(op, "counter"), text(op, "instance"))
			want := op["value"].(float64)
			if got := handle.Value(); got != want {
				label := text(op, "object") + "/" + text(op, "counter")
				if instance := text(op, "instance"); instance != "" {
					label += "." + instance
				}
				t.Errorf("%s = %v, want %v", label, got, want)
			}
		default:
			t.Fatalf("unknown operation %q", op["op"])
		}
	}
}

// sortCounters normalises the one array whose order is not significant.
func sortCounters(payload map[string]any) {
	entries, ok := payload["counters"].([]any)
	if !ok {
		return
	}
	key := func(entry any) string {
		path, _ := entry.(map[string]any)["path"].(map[string]any)
		return text(path, "object") + "|" + text(path, "counter") + "|" + text(path, "instance")
	}
	sort.SliceStable(entries, func(i, j int) bool { return key(entries[i]) < key(entries[j]) })
}

func comparePayload(t *testing.T, stats *Telemetry, options caseOptions, expect map[string]any) {
	t.Helper()

	snapshotAt := time.Now()
	if options.SnapshotAt != "" {
		parsed, err := time.Parse(time.RFC3339, options.SnapshotAt)
		if err != nil {
			t.Fatalf("parsing snapshotAt: %v", err)
		}
		snapshotAt = parsed
	}

	body, err := stats.BuildPayload(snapshotAt)
	if err != nil {
		t.Fatalf("BuildPayload: %v", err)
	}

	// Compared in the shape that goes over the wire, not as live Go values.
	var actual map[string]any
	if err := json.Unmarshal(body, &actual); err != nil {
		t.Fatalf("re-parsing payload: %v", err)
	}

	sortCounters(actual)
	sortCounters(expect)

	if !reflect.DeepEqual(actual, expect) {
		gotJSON, _ := json.MarshalIndent(actual, "", "  ")
		wantJSON, _ := json.MarshalIndent(expect, "", "  ")
		t.Errorf("payload mismatch\n--- got ---\n%s\n--- want ---\n%s", gotJSON, wantJSON)
	}
}

// unrepresentable reports why Go's type system makes a fixture input impossible
// to express, if it does. Reporting that is not the same as passing: it says the
// invalid state cannot be constructed, rather than that it was caught.
func unrepresentable(reject caseReject) string {
	switch reject.Kind {
	case "counter":
		if value, present := reject.Input["value"]; present {
			if _, ok := value.(float64); !ok {
				return "counter values are float64; a non-numeric value cannot be passed"
			}
		}
	case "status":
		if value, present := reject.Input["value"]; present {
			if _, ok := value.(string); !ok {
				return "status values are string; a non-string value cannot be passed"
			}
		}
	}
	return ""
}

func applyReject(stats *Telemetry, reject caseReject) error {
	switch reject.Kind {
	case "counter":
		value, _ := reject.Input["value"].(float64)
		handle, err := stats.Counter(text(reject.Input, "object"), text(reject.Input, "counter"), text(reject.Input, "instance"))
		if err != nil {
			return err
		}
		handle.Set(value)
		return nil
	case "status":
		value, _ := reject.Input["value"].(string)
		return stats.Status(text(reject.Input, "key"), value)
	case "event":
		return stats.Event(text(reject.Input, "message"))
	case "data":
		id, object := dataObjectFrom(reject.Input)
		return stats.Data(id, object)
	}
	return nil
}

func TestConformance(t *testing.T) {
	for _, testCase := range loadCases(t) {
		testCase := testCase

		if len(testCase.Rejects) > 0 {
			for _, reject := range testCase.Rejects {
				reject := reject
				t.Run(testCase.Name+"/"+reject.Reason, func(t *testing.T) {
					if why := unrepresentable(reject); why != "" {
						t.Skipf("unrepresentable in this API: %s", why)
					}
					stats := connect(t, testCase.Options)
					if err := applyReject(stats, reject); err == nil {
						t.Errorf("accepted an input the receiver would discard silently")
					} else {
						t.Logf("rejected with: %v", err)
					}
				})
			}
			continue
		}

		t.Run(testCase.Name, func(t *testing.T) {
			stats := connect(t, testCase.Options)

			if testCase.Operations != nil {
				runOperations(t, stats, testCase.Operations)
			} else {
				applySnapshot(t, stats, testCase.Snapshot)
			}

			if testCase.Expect != nil {
				comparePayload(t, stats, testCase.Options, testCase.Expect)
			}
		})
	}
}
