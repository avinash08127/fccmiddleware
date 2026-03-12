package com.fccmiddleware.edge.adapter.radix

// ---------------------------------------------------------------------------
// Radix XML response parser.
//
// Parses XML responses from the Radix FCC:
//   - FDC_RESP : Transaction management responses (TRN elements, RESP_CODE)
//   - FDCACK   : Pre-auth acknowledgment (ACKCODE, TOKEN)
//   - Product list responses (CMD_CODE=55)
//   - Error responses and signature verification
//
// Key response codes:
//   RESP_CODE=0   : Success (transaction data present)
//   RESP_CODE=205 : Buffer empty (no more transactions)
//
// Implementation follows RX-1.x tasks.
// ---------------------------------------------------------------------------
