package telemetry

import (
	"bytes"
	"context"
	"fmt"
	"io"
	"math"
	"net/http"
	"time"
)

// Error is a send failure. It deliberately carries no endpoint.
//
// The endpoint URL currently carries the sensor identity and is effectively the
// credential (spec/v1.md section 1). net/http puts the request URL into the
// errors it returns, so failures are rebuilt here rather than wrapped — a
// wrapped cause would put the credential into every log that prints the error.
type Error struct {
	// StatusCode is the HTTP status, or 0 for a transport-level failure.
	StatusCode int
	message    string
}

func (e *Error) Error() string { return e.message }

func retryable(status int) bool {
	return status == http.StatusTooManyRequests || status >= 500
}

func backoff(attempt int) time.Duration {
	seconds := math.Min(30, math.Pow(2, float64(attempt-1)))
	return time.Duration(seconds) * time.Second
}

// post sends one payload, retrying transport failures and 5xx responses.
//
// A payload carries absolute current values rather than deltas, which makes the
// request idempotent: a retry after a timeout cannot double-count. That is what
// licenses retrying at all. 4xx responses are not retried, since repeating a
// rejected request will not change the answer.
func (t *Telemetry) post(ctx context.Context, body []byte) error {
	var last *Error

	for attempt := 1; attempt <= t.maxRetries+1; attempt++ {
		err := t.postOnce(ctx, body)
		if err == nil {
			return nil
		}

		sendErr, ok := err.(*Error)
		if !ok {
			return err
		}
		if sendErr.StatusCode != 0 && !retryable(sendErr.StatusCode) {
			return sendErr
		}
		last = sendErr

		if attempt > t.maxRetries {
			break
		}
		select {
		case <-ctx.Done():
			return &Error{message: "netcrunch telemetry send cancelled"}
		case <-time.After(backoff(attempt)):
		}
	}
	return last
}

func (t *Telemetry) postOnce(ctx context.Context, body []byte) error {
	ctx, cancel := context.WithTimeout(ctx, t.timeout)
	defer cancel()

	request, err := http.NewRequestWithContext(ctx, http.MethodPost, t.endpoint, bytes.NewReader(body))
	if err != nil {
		// Rebuilt rather than wrapped: err would name the endpoint.
		return &Error{message: "netcrunch telemetry request could not be built"}
	}
	request.Header.Set("Content-Type", "application/json; charset=utf-8")
	if t.token != "" {
		request.Header.Set("Authorization", "Bearer "+t.token)
	}

	response, err := t.client.Do(request)
	if err != nil {
		reason := "the endpoint was unreachable"
		if ctx.Err() == context.DeadlineExceeded {
			reason = fmt.Sprintf("timed out after %s", t.timeout)
		}
		return &Error{message: "netcrunch telemetry send failed: " + reason}
	}
	defer response.Body.Close()

	// Drain so the connection can be reused. The body is never of interest.
	_, _ = io.Copy(io.Discard, io.LimitReader(response.Body, 4096))

	if response.StatusCode >= 200 && response.StatusCode < 300 {
		return nil
	}
	return &Error{
		StatusCode: response.StatusCode,
		message:    fmt.Sprintf("netcrunch telemetry send failed with HTTP %d", response.StatusCode),
	}
}
