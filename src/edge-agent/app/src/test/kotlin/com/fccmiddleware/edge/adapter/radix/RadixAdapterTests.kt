package com.fccmiddleware.edge.adapter.radix

// ---------------------------------------------------------------------------
// Unit tests for RadixAdapter.
//
// Covers:
//   - IFccAdapter contract compliance (stub returns, no exceptions)
//   - acknowledgeTransactions() always returns true (no-op)
//   - getPumpStatus() always returns empty list
//   - IS_IMPLEMENTED = false while adapter is a stub
//   - Full adapter lifecycle tests once RX-1.x implementation is complete
//
// Test framework: JUnit 5 + MockK
// ---------------------------------------------------------------------------
