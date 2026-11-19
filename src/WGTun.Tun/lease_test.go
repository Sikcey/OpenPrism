package main

import (
	"testing"
	"time"
)

func TestHeartbeatLeaseUsesMonotonicProgress(t *testing.T) {
	now := time.Now()
	future := now.Add(24 * time.Hour)
	var lease leaseTracker
	if !lease.observe(now, future) {
		t.Fatal("initial lease rejected")
	}
	if lease.observe(now.Add(6*time.Second), future) {
		t.Fatal("future file timestamp extended expired heartbeat")
	}
	past := now.Add(-24 * time.Hour)
	if !lease.observe(now.Add(7*time.Second), past) {
		t.Fatal("fresh heartbeat after wall-clock rollback rejected")
	}
	if lease.observe(now.Add(13*time.Second), past) {
		t.Fatal("rollback extended expired heartbeat")
	}
}
