package com.fccmiddleware.edge.adapter.radix

// ---------------------------------------------------------------------------
// Unit tests for RadixXmlParser.
//
// Covers:
//   - Parsing <TRN> elements from FDC_RESP (all field variants)
//   - Parsing <FDCACK> pre-auth responses (ACKCODE, TOKEN)
//   - RESP_CODE handling (0=success, 205=buffer empty, error codes)
//   - Edge cases: empty elements, missing attributes, malformed XML
//   - Product list (CMD_CODE=55) response parsing
//
// Test fixtures stored in: fixtures/
// Test framework: JUnit 5
// ---------------------------------------------------------------------------
