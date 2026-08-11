package telemetry

import (
	"fmt"
	"math"
	"sync"
	"sync/atomic"
)

// Counter is a resolved counter. Resolve it once, keep it, and mutate it on the
// hot path: the cost of an observation is one atomic operation, with no name
// lookup and no allocation. Instrumentation that costs more than that is
// instrumentation people take back out.
//
// A Counter is safe for concurrent use.
type Counter struct {
	object   string
	counter  string
	instance string

	// The value is kept as the bit pattern of a float64 so it can be updated
	// with atomics rather than a mutex.
	bits atomic.Uint64
}

// Value returns the current value.
func (c *Counter) Value() float64 {
	return math.Float64frombits(c.bits.Load())
}

// Set replaces the value.
func (c *Counter) Set(value float64) {
	if validateCounterValue(value) != nil {
		return
	}
	c.bits.Store(math.Float64bits(value))
}

// Add adds delta, which may be negative.
func (c *Counter) Add(delta float64) {
	if validateCounterValue(delta) != nil {
		return
	}
	for {
		old := c.bits.Load()
		next := math.Float64bits(math.Float64frombits(old) + delta)
		if c.bits.CompareAndSwap(old, next) {
			return
		}
	}
}

// Inc adds one.
func (c *Counter) Inc() { c.Add(1) }

// Dec subtracts one.
func (c *Counter) Dec() { c.Add(-1) }

// Max raises the value to v if v is higher, and leaves it alone otherwise.
func (c *Counter) Max(value float64) {
	if validateCounterValue(value) != nil {
		return
	}
	for {
		old := c.bits.Load()
		if math.Float64frombits(old) >= value {
			return
		}
		if c.bits.CompareAndSwap(old, math.Float64bits(value)) {
			return
		}
	}
}

// Min lowers the value to v if v is lower.
func (c *Counter) Min(value float64) {
	if validateCounterValue(value) != nil {
		return
	}
	for {
		old := c.bits.Load()
		if math.Float64frombits(old) <= value {
			return
		}
		if c.bits.CompareAndSwap(old, math.Float64bits(value)) {
			return
		}
	}
}

// Reset sets the value back to zero. The counter keeps being reported; see
// spec/client-model.md section 4 on why zero and absent differ.
func (c *Counter) Reset() { c.Set(0) }

// SelfCount holds one against a counter for as long as it is open.
//
//	lease := stats.SelfCount("Pool", "Leases Active", "")
//	defer lease.Close()
//
// Close is idempotent. That matters more than it looks: a double decrement
// drives the value negative, and a negative gauge drifts away from every
// threshold rather than towards one, so nothing ever reports it.
type SelfCount struct {
	counter *Counter
	once    sync.Once
}

// Close releases the held count. Safe to call more than once.
func (s *SelfCount) Close() {
	s.once.Do(func() { s.counter.Dec() })
}

// Counter returns the underlying counter.
func (s *SelfCount) Counter() *Counter { return s.counter }

// PartCount contributes a movable amount to a counter and withdraws exactly that
// amount on Close, however many times it changed in between.
type PartCount struct {
	counter *Counter

	mu           sync.Mutex
	contribution float64
	closed       bool
}

// Set moves this instance's contribution to value, adjusting the counter by the
// difference. Repeated calls do not accumulate.
func (p *PartCount) Set(value float64) error {
	if err := validateCounterValue(value); err != nil {
		return err
	}
	p.mu.Lock()
	defer p.mu.Unlock()
	if p.closed {
		return fmt.Errorf("part count is closed")
	}
	if value == p.contribution {
		return nil
	}
	p.counter.Add(value - p.contribution)
	p.contribution = value
	return nil
}

// Contribution returns the amount currently contributed.
func (p *PartCount) Contribution() float64 {
	p.mu.Lock()
	defer p.mu.Unlock()
	return p.contribution
}

// Close withdraws the contribution. Safe to call more than once.
func (p *PartCount) Close() {
	p.mu.Lock()
	defer p.mu.Unlock()
	if p.closed {
		return
	}
	p.closed = true
	if p.contribution != 0 {
		p.counter.Add(-p.contribution)
		p.contribution = 0
	}
}

// CategoryCount holds one against a single instance of a counter at a time,
// moving it as the value changes. "How many workers are in each phase" stays
// consistent without anyone remembering to decrement the phase being left.
//
// Buckets are instances of one counter, so Workers/By Phase.parsing and
// Workers/By Phase.writing are siblings rather than unrelated counters.
type CategoryCount struct {
	resolve func(instance string) *Counter

	mu      sync.Mutex
	current string // "" means no instance is held
	closed  bool
}

// Set moves the held count to instance, decrementing whichever instance is being
// left. Passing "" releases the count without holding a new one.
func (c *CategoryCount) Set(instance string) {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.closed || instance == c.current {
		return
	}
	c.release()
	if instance != "" {
		c.resolve(instance).Inc()
		c.current = instance
	}
}

// Current returns the instance currently held, or "" if none.
func (c *CategoryCount) Current() string {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.current
}

// Close releases the held instance. Safe to call more than once.
func (c *CategoryCount) Close() {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.closed {
		return
	}
	c.closed = true
	c.release()
}

// release must be called with c.mu held.
func (c *CategoryCount) release() {
	if c.current == "" {
		return
	}
	c.resolve(c.current).Dec()
	c.current = ""
}
