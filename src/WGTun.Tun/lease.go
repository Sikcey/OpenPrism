package main

import "time"

// File timestamps signal progress; elapsed time comes from Go's monotonic
// clock. A future timestamp or system-clock rollback cannot extend the lease.
type leaseTracker struct {
	stamp    time.Time
	progress time.Time
}

func (l *leaseTracker) observe(now, stamp time.Time) bool {
	if l.progress.IsZero() || !stamp.Equal(l.stamp) {
		l.stamp = stamp
		l.progress = now
	}
	return now.Sub(l.progress) <= 5*time.Second
}
