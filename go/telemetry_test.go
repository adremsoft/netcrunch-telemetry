package telemetry

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

const endpoint = "https://netcrunch.example/api/rest/1/sensors/example@1/update"

func newTest(t *testing.T, options Options) *Telemetry {
	t.Helper()
	if options.Endpoint == "" {
		options.Endpoint = endpoint
	}
	stats, err := New(options)
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	return stats
}

// server returns a test server whose URL embeds a recognisable secret, so a leak
// into an error message is visible.
func server(t *testing.T, handler func(w http.ResponseWriter, count int)) (*httptest.Server, *[]map[string]any) {
	srv, received, _ := serverWithHeaders(t, handler)
	return srv, received
}

func serverWithHeaders(
	t *testing.T,
	handler func(w http.ResponseWriter, count int),
) (*httptest.Server, *[]map[string]any, *[]http.Header) {
	t.Helper()
	var mu sync.Mutex
	received := []map[string]any{}
	headers := []http.Header{}

	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		body, _ := io.ReadAll(r.Body)
		var parsed map[string]any
		_ = json.Unmarshal(body, &parsed)

		mu.Lock()
		received = append(received, parsed)
		headers = append(headers, r.Header.Clone())
		count := len(received)
		mu.Unlock()

		handler(w, count)
	}))
	t.Cleanup(srv.Close)
	return srv, &received, &headers
}

func secretURL(srv *httptest.Server) string {
	return srv.URL + "/api/rest/1/sensors/SENSORSECRET@1/update"
}

// -- construction -----------------------------------------------------------

func TestNewRejectsBadEndpoint(t *testing.T) {
	for _, bad := range []string{"", "not-a-url", "ftp://host/x"} {
		if _, err := New(Options{Endpoint: bad}); err == nil {
			t.Errorf("New(%q) = nil error, want one", bad)
		}
	}
}

func TestRetainMustOutlastFlushInterval(t *testing.T) {
	// A 60s flush against a 1 minute retain would let values expire between sends.
	_, err := New(Options{Endpoint: endpoint, FlushInterval: time.Minute, Retain: time.Minute})
	if err == nil {
		t.Fatal("expected an error when retain does not exceed the flush interval")
	}
}

// -- counter handles --------------------------------------------------------

func TestSamePathResolvesToSameHandle(t *testing.T) {
	stats := newTest(t, Options{})

	first := stats.MustCounter("Queue", "Depth", "inbound")
	second := stats.MustCounter("Queue", "Depth", "inbound")
	if first != second {
		t.Error("resolving the same path twice returned different handles")
	}
	if first == stats.MustCounter("Queue", "Depth", "outbound") {
		t.Error("different instances shared a handle")
	}
}

func TestMaxAndMinMoveOneWay(t *testing.T) {
	stats := newTest(t, Options{})

	peak := stats.MustCounter("SNMP", "Peak ms", "")
	peak.Max(120)
	peak.Max(90)
	peak.Max(200)
	if got := peak.Value(); got != 200 {
		t.Errorf("Max: got %v, want 200", got)
	}

	floor := stats.MustCounter("SNMP", "Floor ms", "")
	floor.Set(100)
	floor.Min(150)
	floor.Min(40)
	floor.Min(70)
	if got := floor.Value(); got != 40 {
		t.Errorf("Min: got %v, want 40", got)
	}
}

// -- lifetime-bound aggregates ---------------------------------------------

func TestSelfCountClosesIdempotently(t *testing.T) {
	// defer and an explicit Close can both fire on the same value. A double
	// decrement drives the count negative, which drifts away from every threshold
	// rather than towards one, so nothing would ever report it.
	stats := newTest(t, Options{})
	handle := stats.MustCounter("Pool", "Leases Active", "")

	lease := stats.SelfCount("Pool", "Leases Active", "")
	if got := handle.Value(); got != 1 {
		t.Fatalf("after SelfCount: got %v, want 1", got)
	}

	lease.Close()
	lease.Close()
	lease.Close()
	if got := handle.Value(); got != 0 {
		t.Errorf("after repeated Close: got %v, want 0", got)
	}
}

func TestPartCountWithdrawsItsContribution(t *testing.T) {
	stats := newTest(t, Options{})
	handle := stats.MustCounter("Cache", "Entries", "")
	handle.Set(1000)

	part := stats.PartCount("Cache", "Entries", "")
	for _, value := range []float64{5, 9, 3} {
		if err := part.Set(value); err != nil {
			t.Fatalf("Set(%v): %v", value, err)
		}
	}
	if got := handle.Value(); got != 1003 {
		t.Errorf("after sets: got %v, want 1003", got)
	}

	part.Close()
	if got := handle.Value(); got != 1000 {
		t.Errorf("after Close: got %v, want 1000 (the pre-existing value must survive)", got)
	}
	if err := part.Set(1); err == nil {
		t.Error("Set after Close should report an error rather than silently reopening")
	}
}

func TestCategoryMovesBetweenInstances(t *testing.T) {
	stats := newTest(t, Options{})
	phase := stats.Category("Workers", "By Phase")

	phase.Set("parsing")
	if got := stats.MustCounter("Workers", "By Phase", "parsing").Value(); got != 1 {
		t.Fatalf("parsing: got %v, want 1", got)
	}

	phase.Set("writing")
	if got := stats.MustCounter("Workers", "By Phase", "parsing").Value(); got != 0 {
		t.Errorf("parsing after move: got %v, want 0", got)
	}
	if got := stats.MustCounter("Workers", "By Phase", "writing").Value(); got != 1 {
		t.Errorf("writing: got %v, want 1", got)
	}

	phase.Close()
	if got := stats.MustCounter("Workers", "By Phase", "writing").Value(); got != 0 {
		t.Errorf("writing after Close: got %v, want 0", got)
	}
}

// -- payload ----------------------------------------------------------------

func TestEmptyCollectionsAreOmitted(t *testing.T) {
	stats := newTest(t, Options{})

	body, err := stats.BuildPayload(time.Now())
	if err != nil {
		t.Fatalf("BuildPayload: %v", err)
	}
	if got := string(body); got != `{"retain":5,"remove":1440}` {
		t.Errorf("empty payload: got %s", got)
	}
}

func TestTimestampMessageCarriesNoMilliseconds(t *testing.T) {
	stats := newTest(t, Options{})
	observed := time.Date(2026, 8, 10, 9, 14, 22, 512_000_000, time.UTC)
	if err := stats.Timestamp("Sync", "Age s", "Last Sync", observed); err != nil {
		t.Fatalf("Timestamp: %v", err)
	}

	body, _ := stats.BuildPayload(time.Date(2026, 8, 10, 9, 15, 54, 0, time.UTC))
	var parsed map[string]any
	_ = json.Unmarshal(body, &parsed)

	statuses := parsed["statuses"].(map[string]any)
	message := statuses["Last Sync"].(map[string]any)["message"]
	if message != "2026-08-10T09:14:22Z" {
		t.Errorf("message: got %v", message)
	}

	// Milliseconds are dropped from the message but still count towards the age:
	// 91.488s rounds to 91, not the 92 a whole-second observation would give.
	age := parsed["counters"].([]any)[0].(map[string]any)["value"]
	if age != float64(91) {
		t.Errorf("age: got %v, want 91", age)
	}
}

// -- sending ----------------------------------------------------------------

func TestFlushClearsEventsButKeepsCountersAndStatuses(t *testing.T) {
	srv, received := server(t, func(w http.ResponseWriter, _ int) { w.WriteHeader(http.StatusOK) })
	stats := newTest(t, Options{Endpoint: srv.URL})

	stats.MustCounter("Job", "Items", "").Set(7)
	_ = stats.Status("Job", "OK")
	_ = stats.Event("started")

	if err := stats.Flush(context.Background()); err != nil {
		t.Fatalf("first flush: %v", err)
	}
	if err := stats.Flush(context.Background()); err != nil {
		t.Fatalf("second flush: %v", err)
	}

	if len(*received) != 2 {
		t.Fatalf("requests: got %d, want 2", len(*received))
	}
	if _, present := (*received)[0]["events"]; !present {
		t.Error("first payload should carry the event")
	}
	if _, present := (*received)[1]["events"]; present {
		t.Error("second payload should not repeat the event")
	}
	if _, present := (*received)[1]["counters"]; !present {
		t.Error("counters must keep being reported")
	}
}

func TestTokenIsSentAsBearer(t *testing.T) {
	srv, _, headers := serverWithHeaders(t, func(w http.ResponseWriter, _ int) { w.WriteHeader(http.StatusOK) })

	stats := newTest(t, Options{Endpoint: srv.URL, Token: "TOKENSECRET"})
	_ = stats.Status("Job", "OK")
	if err := stats.Flush(context.Background()); err != nil {
		t.Fatalf("Flush: %v", err)
	}

	if got := (*headers)[0].Get("Authorization"); got != "Bearer TOKENSECRET" {
		t.Errorf("Authorization = %q, want %q", got, "Bearer TOKENSECRET")
	}
}

func TestNoTokenMeansNoAuthorizationHeader(t *testing.T) {
	srv, _, headers := serverWithHeaders(t, func(w http.ResponseWriter, _ int) { w.WriteHeader(http.StatusOK) })

	stats := newTest(t, Options{Endpoint: srv.URL})
	_ = stats.Status("Job", "OK")
	if err := stats.Flush(context.Background()); err != nil {
		t.Fatalf("Flush: %v", err)
	}

	if got := (*headers)[0].Get("Authorization"); got != "" {
		t.Errorf("Authorization = %q, want it absent", got)
	}
}

func TestErrorsNeverCarryTheEndpoint(t *testing.T) {
	srv, _ := server(t, func(w http.ResponseWriter, _ int) { w.WriteHeader(http.StatusUnauthorized) })
	stats := newTest(t, Options{Endpoint: secretURL(srv)})
	_ = stats.Status("Job", "OK")

	err := stats.Flush(context.Background())
	if err == nil {
		t.Fatal("expected an error")
	}

	var sendErr *Error
	if !errors.As(err, &sendErr) || sendErr.StatusCode != http.StatusUnauthorized {
		t.Fatalf("got %#v, want *Error with status 401", err)
	}
	// The URL is currently the credential; it must not reach a log.
	if strings.Contains(err.Error(), "SENSORSECRET") || strings.Contains(err.Error(), "127.0.0.1") {
		t.Errorf("error leaked the endpoint: %v", err)
	}
}

func TestRetryPolicy(t *testing.T) {
	rejected, rejectedRequests := server(t, func(w http.ResponseWriter, _ int) { w.WriteHeader(http.StatusBadRequest) })
	stats := newTest(t, Options{Endpoint: rejected.URL, MaxRetries: 3})
	_ = stats.Status("Job", "OK")

	if err := stats.Flush(context.Background()); err == nil {
		t.Fatal("expected a 400 to fail")
	}
	if len(*rejectedRequests) != 1 {
		t.Errorf("a rejected request must not be repeated: got %d attempts", len(*rejectedRequests))
	}

	flaky, flakyRequests := server(t, func(w http.ResponseWriter, count int) {
		if count == 1 {
			w.WriteHeader(http.StatusServiceUnavailable)
			return
		}
		w.WriteHeader(http.StatusOK)
	})
	retrying := newTest(t, Options{Endpoint: flaky.URL, MaxRetries: 1})
	_ = retrying.Status("Job", "OK")

	if err := retrying.Flush(context.Background()); err != nil {
		t.Fatalf("a 503 should be retried and then succeed: %v", err)
	}
	if len(*flakyRequests) != 2 {
		t.Errorf("attempts: got %d, want 2", len(*flakyRequests))
	}
}

func TestNothingStagedMeansNothingSent(t *testing.T) {
	srv, received := server(t, func(w http.ResponseWriter, _ int) { w.WriteHeader(http.StatusOK) })
	stats := newTest(t, Options{Endpoint: srv.URL})

	if err := stats.Flush(context.Background()); err != nil {
		t.Fatalf("Flush: %v", err)
	}
	if len(*received) != 0 {
		t.Errorf("requests: got %d, want 0", len(*received))
	}
}

// TestConcurrentUse is about the race detector rather than its assertions.
func TestConcurrentUse(t *testing.T) {
	var served atomic.Int64
	srv, _ := server(t, func(w http.ResponseWriter, _ int) {
		served.Add(1)
		w.WriteHeader(http.StatusOK)
	})
	stats := newTest(t, Options{Endpoint: srv.URL})

	handle := stats.MustCounter("Load", "Operations", "")

	var wg sync.WaitGroup
	for worker := 0; worker < 8; worker++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			for i := 0; i < 200; i++ {
				handle.Inc()
				lease := stats.SelfCount("Load", "In Flight", "")
				_ = stats.Status("Worker", "OK")
				lease.Close()
			}
			_ = stats.Flush(context.Background())
		}()
	}
	wg.Wait()

	if got := handle.Value(); got != 1600 {
		t.Errorf("operations: got %v, want 1600", got)
	}
	if got := stats.MustCounter("Load", "In Flight", "").Value(); got != 0 {
		t.Errorf("in flight: got %v, want 0", got)
	}
}
