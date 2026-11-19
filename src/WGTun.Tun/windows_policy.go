package main

import (
	"encoding/json"
	"fmt"
	"regexp"
)

// CompileWindows adapts WGTun's exact Windows exe names/paths to the pinned
// sing-box matcher's case-sensitive API. Generated folder regexes already use
// (?i). It does not mutate the export or change upstream/production matching.
func CompileWindows(input Export) (*Policy, error) {
	adapted := input
	adapted.Rules = make([]json.RawMessage, 0, len(input.Rules))
	for _, raw := range input.Rules {
		var fields map[string]json.RawMessage
		if err := json.Unmarshal(raw, &fields); err != nil {
			return nil, err
		}
		for _, key := range []string{"process_path", "process_name"} {
			value, exists := fields[key]
			if !exists {
				continue
			}
			if _, exists = fields["process_path_regex"]; exists {
				return nil, fmt.Errorf("combined process predicates require explicit adaptation")
			}
			var paths []string
			if err := json.Unmarshal(value, &paths); err != nil {
				return nil, err
			}
			patterns := make([]string, 0, len(paths))
			for _, path := range paths {
				expression := "(?i)^" + regexp.QuoteMeta(path) + "$"
				if key == "process_name" {
					expression = "(?i)(?:^|[\\\\/])" + regexp.QuoteMeta(path) + "$"
				}
				patterns = append(patterns, expression)
			}
			encoded, err := json.Marshal(patterns)
			if err != nil {
				return nil, err
			}
			delete(fields, key)
			fields["process_path_regex"] = encoded
		}
		encoded, err := json.Marshal(fields)
		if err != nil {
			return nil, err
		}
		adapted.Rules = append(adapted.Rules, encoded)
	}
	return Compile(adapted)
}
