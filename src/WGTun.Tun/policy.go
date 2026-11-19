// Offline research adapter. No driver, WFP filters, TUN, or system routes are opened.
package main

import (
	"context"
	"encoding/json"
	"fmt"
	"time"

	"github.com/sagernet/sing-box/adapter"
	"github.com/sagernet/sing-box/log"
	"github.com/sagernet/sing-box/option"
	sbRule "github.com/sagernet/sing-box/route/rule"
	sbJSON "github.com/sagernet/sing/common/json"
)

type Export struct {
	AppRoot string            `json:"appRoot"`
	Scope   string            `json:"scope"`
	Final   string            `json:"final"`
	Rules   []json.RawMessage `json:"rules"`
}

type Decision struct {
	Intercept bool
	Reason    string
}

type Policy struct {
	rules []adapter.Rule
	final string
}

func Compile(input Export) (*Policy, error) {
	if input.Final != "direct" && input.Final != "active" {
		return nil, fmt.Errorf("unsupported final")
	}
	p := &Policy{final: input.Final}
	logger := log.NewNOPFactory().NewLogger("offline-steering")
	for _, raw := range input.Rules {
		options, err := sbJSON.UnmarshalExtendedContext[option.Rule](context.Background(), raw)
		if err != nil {
			return nil, err
		}
		compiled, err := sbRule.NewRule(context.Background(), logger, options, true)
		if err != nil {
			return nil, err
		}
		switch action := compiled.Action().(type) {
		case *sbRule.RuleActionRoute:
			if action.Outbound != "direct" && action.Outbound != "active" {
				return nil, fmt.Errorf("unsupported outbound")
			}
		default:
			// This experiment is deliberately selected-only / except-selected.
			// DNS hijacking needs a separate isolation contract.
			return nil, fmt.Errorf("unsupported action %s", compiled.Action().Type())
		}
		p.rules = append(p.rules, compiled)
	}
	return p, nil
}

// Decide uses the real pinned sing-box matcher, not a second implementation.
// Calling it before interception requires trustworthy kernel metadata, which this
// fixture supplies synthetically. It is not safe to run Go in a kernel callback.
func (p *Policy) Decide(metadata adapter.InboundContext) Decision {
	for _, rule := range p.rules {
		metadata.ResetRuleCache()
		if rule.Match(&metadata) {
			action := rule.Action().(*sbRule.RuleActionRoute)
			return Decision{action.Outbound == "active", "rule: " + rule.String()}
		}
	}
	return Decision{p.final == "active", "default: " + p.final}
}

// Gate models a driver-owned lease with explicit generation invalidation.
// The caller supplies a monotonic clock value. No per-flow round trip is modeled.
type Gate struct {
	policy     *Policy
	generation uint64
	expires    time.Time
}

func (g *Gate) Activate(p *Policy, generation uint64, now time.Time, lease time.Duration) error {
	if p == nil || generation <= g.generation || lease <= 0 || lease > 5*time.Second {
		return fmt.Errorf("invalid policy generation or lease")
	}
	g.policy, g.generation, g.expires = p, generation, now.Add(lease)
	return nil
}

func (g *Gate) Disconnect() { g.policy = nil; g.generation++ }

func (g *Gate) Decide(generation uint64, now time.Time, metadata adapter.InboundContext) Decision {
	if g.policy == nil || generation != g.generation || !now.Before(g.expires) {
		return Decision{false, "inactive, stale generation, or expired lease"}
	}
	return g.policy.Decide(metadata)
}
