package telemetry_test

import (
	"context"
	"log"
	"os"
	"time"

	telemetry "github.com/adremsoft/netcrunch-telemetry/go"
)

// The shape worth copying for a long-running service: resolve handles once,
// mutate them on the hot path, and let the flush loop do the sending.
func Example() {
	stats, err := telemetry.New(telemetry.Options{
		// The URL is effectively a credential — keep it in the environment.
		Endpoint:      os.Getenv("NC_TELEMETRY_URL"),
		FlushInterval: time.Minute,
		Retain:        5 * time.Minute,
		OnError:       func(err error) { log.Printf("telemetry: %v", err) },
	})
	if err != nil {
		log.Fatal(err)
	}
	defer stats.Close(context.Background())

	// Resolved once. This is what the hot path touches.
	requests := stats.MustCounter("HTTP", "Requests", "")
	requests.Inc()

	// Only a status will raise an alert. Counters alone will not.
	_ = stats.Status("Service", "OK", telemetry.StatusOptions{Message: "warm"})
}

// SelfCount ties the decrement to the scope, so the gauge stays right even when
// the handler returns early or panics.
func ExampleTelemetry_SelfCount() {
	stats, _ := telemetry.New(telemetry.Options{Endpoint: "https://netcrunch.example/x"})

	handle := func() {
		lease := stats.SelfCount("Pool", "Leases Active", "")
		defer lease.Close()

		// ... work ...
	}
	handle()
}

// Category keeps "how many workers are in each phase" consistent without anyone
// remembering to decrement the phase being left.
func ExampleTelemetry_Category() {
	stats, _ := telemetry.New(telemetry.Options{Endpoint: "https://netcrunch.example/x"})

	phase := stats.Category("Workers", "By Phase")
	defer phase.Close()

	phase.Set("parsing")
	phase.Set("writing") // parsing drops to 0, writing rises to 1
}

// A scheduled job reports its outcome and, by not reporting at all when it fails
// to run, lets the retain time raise the alert on its own.
func ExampleTelemetry_Flush() {
	stats, _ := telemetry.New(telemetry.Options{
		Endpoint: os.Getenv("NC_TELEMETRY_URL"),
		// 25 hours: a nightly job gets an hour of grace before the status expires.
		Retain: 25 * time.Hour,
	})

	processed := 1204
	_ = stats.Table("outcome", telemetry.Table{
		Name:    "Import Outcome",
		Columns: []any{"Stage", "Items"},
		Rows:    [][]any{{"imported", processed}, {"failed", 3}},
	})
	_ = stats.Status("Nightly Import", "OK", telemetry.StatusOptions{
		Message: "1204 items",
	})
	_ = stats.Timestamp("Nightly Import", "Last Success Age s", "Last Clean Run", time.Now())

	if err := stats.Flush(context.Background()); err != nil {
		log.Printf("telemetry: %v", err)
	}
}
