package telemetry

import (
	"fmt"
	"math"
	"reflect"
	"strings"
)

const (
	maxStatusKeyLength = 500

	// Beyond this the receiver slices arrays without telling anyone.
	maxDataEntries = 1024
)

// dataTypeMembers maps a data object type to the members carrying its payload.
// It is also the set of accepted types.
var dataTypeMembers = map[string][]string{
	"table":       {"columns", "rows"},
	"time-series": {"timestamps", "values"},
	"category":    {"categories", "values"},
}

// Validation here mirrors what the NetCrunch receiver discards *silently* — an
// empty status value, a key it reserves, an event with no message. A library
// that forwarded those would lose data with nothing raised at either end, so
// each is rejected at the call site, where the caller can still do something
// about it.
//
// Go's type system removes several of the checks the other implementations need:
// a counter value cannot be a string, and a status value cannot be a number,
// because the signatures do not allow it.

func validateCounterPath(object, counter string) error {
	if strings.TrimSpace(object) == "" {
		return fmt.Errorf("counter object is required and must not be empty")
	}
	if strings.TrimSpace(counter) == "" {
		return fmt.Errorf("counter name is required and must not be empty")
	}
	return nil
}

func validateCounterValue(value float64) error {
	// NaN and Inf have no JSON representation; encoding/json refuses them, and a
	// silent zero would be worse than the error.
	if math.IsNaN(value) || math.IsInf(value, 0) {
		return fmt.Errorf("counter value must be finite, got %v", value)
	}
	return nil
}

func validateStatusKey(key string) error {
	if strings.TrimSpace(key) == "" {
		return fmt.Errorf("status key is required and must not be empty")
	}
	if strings.HasPrefix(key, "@") {
		return fmt.Errorf("status key %q is reserved — NetCrunch uses the \"@\" prefix internally", key)
	}
	if len(key) > maxStatusKeyLength {
		return fmt.Errorf("status key is %d characters; NetCrunch truncates at %d", len(key), maxStatusKeyLength)
	}
	return nil
}

func validateStatusValue(value string) error {
	if value == "" {
		return fmt.Errorf("status value must not be empty — NetCrunch discards empty statuses without reporting it")
	}
	return nil
}

func validateEventMessage(message string) error {
	if strings.TrimSpace(message) == "" {
		return fmt.Errorf("event message must not be empty — NetCrunch discards such events without reporting it")
	}
	return nil
}

// validateDataObject checks a data object against spec/v1.md section 6.
func validateDataObject(id, objectType string, members map[string]any) error {
	if strings.TrimSpace(id) == "" {
		return fmt.Errorf("data object id is required and must not be empty")
	}
	if objectType == "internal" {
		return fmt.Errorf(`the "internal" data object type is reserved for NetCrunch's own sensors`)
	}

	required, ok := dataTypeMembers[objectType]
	if !ok {
		return fmt.Errorf(
			"unknown data object type %q — NetCrunch discards these with only a server-side warning; use table, time-series or category",
			objectType,
		)
	}

	lengths := make(map[string]int, len(required))
	for _, member := range required {
		length, ok := memberLength(members[member])
		if !ok {
			return fmt.Errorf("a %s data object requires %q to be an array", objectType, member)
		}
		if length > maxDataEntries {
			return fmt.Errorf(
				"%q has %d entries; NetCrunch truncates at %d without reporting it",
				member, length, maxDataEntries,
			)
		}
		lengths[member] = length
	}

	// Ragged parallel arrays are the dangerous case: nothing errors anywhere and
	// the chart quietly plots the wrong thing.
	if objectType == "table" {
		width := lengths["columns"]
		rows, _ := members["rows"].([]any)
		for i, row := range rows {
			cells, ok := row.([]any)
			if !ok {
				return fmt.Errorf("table row %d must be an array of cells", i)
			}
			if len(cells) != width {
				return fmt.Errorf("table row %d has %d cells but there are %d columns", i, len(cells), width)
			}
		}
		return nil
	}

	left, right := required[0], required[1]
	if lengths[left] != lengths[right] {
		return fmt.Errorf("%q has %d entries but %q has %d; they must match", left, lengths[left], right, lengths[right])
	}
	return nil
}

// isNil reports whether value is nil, including a typed nil held in an interface
// — which `value == nil` alone does not catch.
func isNil(value any) bool {
	if value == nil {
		return true
	}
	switch reflected := reflect.ValueOf(value); reflected.Kind() {
	case reflect.Chan, reflect.Func, reflect.Interface, reflect.Map, reflect.Pointer, reflect.Slice:
		return reflected.IsNil()
	default:
		return false
	}
}

// memberLength reports the length of a data object member, accepting the several
// slice types the typed constructors and the generic Data method both produce.
func memberLength(value any) (int, bool) {
	switch typed := value.(type) {
	case nil:
		return 0, false
	case []any:
		return len(typed), true
	case []string:
		return len(typed), true
	case []float64:
		return len(typed), true
	case []int64:
		return len(typed), true
	case [][]any:
		return len(typed), true
	default:
		return 0, false
	}
}
