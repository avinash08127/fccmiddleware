# Radix FDC REST API – Integration Contract

> Extracted from: `Spec_FDC_RESTAPI v1.3.1 (27.02.2026)`
> FDC Firmware requirement: **v3.49+**

---

## 1. Transport & Authentication

| Aspect | Detail |
|--------|--------|
| **Protocol** | HTTP POST (REST) |
| **Content-Type** | `Application/xml` |
| **Network** | Host (EAS) must be on the **same LAN** as FDC |
| **IP allow-list** | EAS IP must be whitelisted in FDC configuration |
| **Auth mechanism** | HMAC-style SHA1 signature per message |
| **NTP dependency** | None – protocol uses tokens, not timestamps for ordering |

### 1.1 Mandatory Request Headers

| Header | Value | Notes |
|--------|-------|-------|
| `Content-Type` | `Application/xml` | Always required |
| `Content-Length` | `<body length in bytes>` | Standard HTTP, always required |
| `USN-Code` | `1 – 999999` | Must match FDC station number in system config |
| `Operation` | `1` / `2` / `3` / `4` / `5` / `Authorize` | See operation codes below |

### 1.2 Signature Calculation

**For Operations 1–5 (Transaction, Products, Day Close, ATG, CSR):**

```
SIGNATURE = SHA1( <REQ>...</REQ> + SECRET_PASSWORD )
```

- Input is the literal XML content from `<REQ>` through `</REQ>` (inclusive), concatenated with the secret word immediately after `</REQ>` (no space).
- All whitespace and special characters are significant.

**For External Authorization (Operation "Authorize"):**

```
FDCSIGNATURE = SHA1( <AUTH_DATA>...</AUTH_DATA> + SECRET_PASSWORD )
```

- Input is the literal XML content from `<AUTH_DATA>` through `</AUTH_DATA>` (inclusive), concatenated with the secret word immediately after `</AUTH_DATA>` (no space).

---

## 2. Port Assignment

| Function | Port |
|----------|------|
| External Authorization | Configured port (e.g. `5002`) |
| Transaction Management / Products / ATG / CSR | Configured port **+ 1** (e.g. `5003`) |

---

## 3. Operation & Command Code Reference

### 3.1 Operation Codes (Header: `Operation`)

| Code | Function |
|------|----------|
| `1` | Transaction Management |
| `2` | Products and Prices |
| `3` | Day Close |
| `4` | ATG Data |
| `5` | CSR Data |
| `Authorize` | External Authorization (preset/auth) |

### 3.2 Command Codes (`<CMD_CODE>`)

| CMD_CODE | Direction | Description |
|----------|-----------|-------------|
| `10` | Host → FDC | Request Transaction (ON_DEMAND mode only) |
| `20` | Host → FDC | Mode Change (allowed in all modes) |
| `30` | Host → FDC | Request Tank Data (ATG) |
| `35` | Host → FDC | Request ATG Delivery |
| `40` | Host → FDC | Request CSR Data |
| `55` | Host → FDC | Read Prices and Products |
| `66` | Host → FDC | Write/Change Prices and Products |
| `77` | Host → FDC | Day Close |
| `201` | Host → FDC | ACK – Success (host confirming receipt) |

### 3.3 Response Codes

| Code | Meaning |
|------|---------|
| `0` | Success (External Authorization only) |
| `201` | OK / Success |
| `205` | No data available (no TRN / no delivery / no CSR) |
| `30` | Unsolicited transaction push from FDC |

### 3.4 Error Codes

| Code | Meaning |
|------|---------|
| `206` | Transaction mode error |
| `207` | Product data error / ACK code data error |
| `251` | Signature error |
| `252` | Command code error |
| `253` | Token error |
| `255` | Bad XML format |
| `256` | Bad header format |
| `258` | Pump not ready |
| `260` | DSB is offline |

---

## 4. Contract: Transaction Management (`Operation: 1`)

### 4.1 Transaction Modes

| Mode | Value | Behavior |
|------|-------|----------|
| ON_DEMAND | `1` | Host polls FDC for transactions |
| UNSOLICITED | `2` | FDC pushes transactions to host |
| OFF | `0` | No transaction transfer |

### 4.2 Mode Change (CMD_CODE 20)

**Host → FDC Request:**
```xml
<?xml version="1.0" encoding="UTF-8"?>
<HOST_REQ>
    <REQ>
        <CMD_CODE>20</CMD_CODE>
        <CMD_NAME>MODE_CNG</CMD_NAME>
        <MODE>{0|1|2}</MODE>
        <TOKEN>{unique_token}</TOKEN>
    </REQ>
    <SIGNATURE>{sha1_hash}</SIGNATURE>
</HOST_REQ>
```

**FDC → Host Response (success):**
```xml
<?xml version="1.0" encoding="UTF-8"?>
<FDC_RESP>
  <TABLE VERSION="1.0">
    <ANS RESP_CODE="201" RESP_MSG="ON_DEMAND" TOKEN="{token}" />
  </TABLE>
  <SIGNATURE>{sha1_hash}</SIGNATURE>
</FDC_RESP>
```

- Allowed in **all modes** (including OFF).
- `RESP_MSG` reflects the new mode name.

### 4.3 Poll Transaction – ON_DEMAND (CMD_CODE 10)

**Host → FDC Request:**
```xml
<?xml version="1.0" encoding="UTF-8"?>
<HOST_REQ>
    <REQ>
        <CMD_CODE>10</CMD_CODE>
        <CMD_NAME>TRN_REQ</CMD_NAME>
        <TOKEN>{unique_token}</TOKEN>
    </REQ>
    <SIGNATURE>{sha1_hash}</SIGNATURE>
</HOST_REQ>
```

- Only works in mode `1` (ON_DEMAND). Other modes ignore this command.
- Returns the **oldest transaction** from FDC buffer.

**FDC → Host Response (transaction available):**
```xml
<?xml version="1.0" encoding="UTF-8"?>
<FDC_RESP>
  <TABLE VERSION="1.0">
    <ANS RESP_CODE="201" RESP_MSG="SUCCESS" TOKEN="{token}" />
    <TRN {transaction_attributes} />
    <RFID_CARD {card_attributes} />
    <DISCOUNT {discount_attributes} />
    <CUST_DATA USED="{0|1}">
  </TABLE>
  <SIGNATURE>{sha1_hash}</SIGNATURE>
</FDC_RESP>
```

**Host → FDC ACK (after successful receipt):**
```xml
<?xml version="1.0" encoding="UTF-8"?>
<HOST_REQ>
    <REQ>
        <CMD_CODE>201</CMD_CODE>
        <CMD_NAME>SUCCESS</CMD_NAME>
        <TOKEN>{token}</TOKEN>
    </REQ>
    <SIGNATURE>{sha1_hash}</SIGNATURE>
</HOST_REQ>
```

**FDC → Host Response (no transaction):**
```xml
<ANS RESP_CODE="205" RESP_MSG="NO TRN AVAILABLE" TOKEN="{token}" />
<TRN />
<RFID_CARD />
<DISCOUNT />
```

### 4.4 Unsolicited Transaction Push (FDC → Host)

FDC pushes to host automatically when in mode `2` (UNSOLICITED).

**FDC → Host Push:**
```xml
<ANS RESP_CODE="30" RESP_MSG="UNSOL_TRN" TOKEN="{token}" />
<TRN {transaction_attributes} />
<RFID_CARD {card_attributes} />
<DISCOUNT {discount_attributes} />
<CUST_DATA USED="{0|1}">
```

- Host must reply with CMD_CODE `201` ACK (same as ON_DEMAND).
- **On error:** host does NOT respond; FDC retries after timeout.

### 4.5 Transaction Data Model (`<TRN>` attributes)

| Attribute | Type | Description |
|-----------|------|-------------|
| `AMO` | Decimal | Transaction amount (e.g. `"30000.0"`) |
| `EFD_ID` | String | EFD identifier |
| `FDC_DATE` | Date | Transaction date (`YYYY-MM-DD`) |
| `FDC_NAME` | String | FDC device name |
| `FDC_NUM` | String | FDC device number |
| `FDC_PROD` | Integer | Product index |
| `FDC_PROD_NAME` | String | Product name (e.g. `"UNLEADED"`) |
| `FDC_SAVE_NUM` | String | FDC save number |
| `FDC_TANK` | String | Tank identifier (may be empty) |
| `FDC_TIME` | Time | Transaction time (`HH:MM:SS`) |
| `FP` | Integer | Filling point number |
| `NOZ` | Integer | Nozzle number |
| `PRICE` | Integer | Unit price |
| `PUMP_ADDR` | Integer | Pump address |
| `RDG_DATE` | Date | RDG date (`YYYY-MM-DD`) |
| `RDG_ID` | String | RDG identifier |
| `RDG_INDEX` | Integer | RDG index |
| `RDG_PROD` | Integer | RDG product number |
| `RDG_SAVE_NUM` | String | RDG save number |
| `RDG_TIME` | Time | RDG time (`HH:MM:SS`) |
| `REG_ID` | String | Registration ID |
| `ROUND_TYPE` | Integer | Rounding type |
| `VOL` | Decimal | Volume dispensed (e.g. `"15.54"`) |

### 4.6 RFID Card Data Model (`<RFID_CARD>` attributes)

| Attribute | Type | Description |
|-----------|------|-------------|
| `CARD_TYPE` | Integer | Card type code |
| `CUST_CONTACT` | String | Customer contact (may be empty) |
| `CUST_ID` | String | Customer identifier |
| `CUST_IDTYPE` | Integer | Customer ID type (1–6) |
| `CUST_NAME` | String | Customer name (may be empty) |
| `DISCOUNT` | Integer | Discount value |
| `DISCOUNT_TYPE` | Integer | Discount type |
| `NUM` | String | Card number |
| `NUM_10` | String | Card number (decimal) |
| `PAY_METHOD` | Integer | Payment method code |
| `PRODUCT_ENABLED` | Integer | Product-enabled flag |
| `USED` | Integer | Card-used flag (`0` or `1`) |

### 4.7 Discount Data Model (`<DISCOUNT>` attributes)

| Attribute | Type | Description |
|-----------|------|-------------|
| `AMO_DISCOUNT` | Decimal | Discount amount |
| `AMO_NEW` | Decimal | Amount after discount |
| `AMO_ORIGIN` | Decimal | Original amount |
| `DISCOUNT_TYPE` | Integer | Discount type |
| `PRICE_DISCOUNT` | Integer | Price discount |
| `PRICE_NEW` | Integer | Price after discount |
| `PRICE_ORIGIN` | Integer | Original price |
| `VOL_ORIGIN` | Decimal | Original volume |

---

## 5. Contract: Products & Prices (`Operation: 2`)

### 5.1 Read Products & Prices (CMD_CODE 55)

**Host → FDC Request:**
```xml
<?xml version="1.0" encoding="UTF-8"?>
<HOST_REQ>
  <REQ>
    <CMD_CODE>55</CMD_CODE>
    <CMD_NAME>PROD and PRICE REQ</CMD_NAME>
    <TOKEN>{unique_token}</TOKEN>
  </REQ>
  <SIGNATURE>{sha1_hash}</SIGNATURE>
</HOST_REQ>
```

**FDC → Host Response (success):**
```xml
<FDC_RESP>
  <TABLE VERSION="1.0">
    <ANS RESP_CODE="201" RESP_MSG="REQ OK" TOKEN="{token}" />
    <PRODUCT ID="1" NAME="Product_1" PRICE="10.99" />
    <PRODUCT ID="2" NAME="Product_2" PRICE="22.99" />
    <!-- only active products are returned -->
  </TABLE>
  <SIGNATURE>{sha1_hash}</SIGNATURE>
</FDC_RESP>
```

- Only **active (activated)** products are included. Inactive products are omitted.

### 5.2 Write/Change Product & Price (CMD_CODE 66)

**Host → FDC Request (one product per request):**
```xml
<?xml version="1.0" encoding="UTF-8"?>
<HOST_REQ>
  <REQ>
    <CMD_CODE>66</CMD_CODE>
    <CMD_NAME>PROD and PRICE CHANGE</CMD_NAME>
    <TOKEN>{unique_token}</TOKEN>
    <PRODUCT_ID>{1-N}</PRODUCT_ID>
    <PRODUCT_NAME>{name}</PRODUCT_NAME>
    <PRODUCT_PRICE>{price}</PRODUCT_PRICE>
    <PRODUCT_ACTIVE>{YES|NO}</PRODUCT_ACTIVE>
    <PRODUCT_ENABLE>{YES|NO}</PRODUCT_ENABLE>
  </REQ>
  <SIGNATURE>{sha1_hash}</SIGNATURE>
</HOST_REQ>
```

| Field | Required | Description |
|-------|----------|-------------|
| `PRODUCT_ID` | Yes | Product index to modify |
| `PRODUCT_NAME` | Yes | Product display name |
| `PRODUCT_PRICE` | Yes | New price |
| `PRODUCT_ACTIVE` | Yes | `YES` = visible in reports; `NO` = hidden (ensure no nozzles attached) |
| `PRODUCT_ENABLE` | Yes | `YES` = nozzles authorized; `NO` = visible but nozzles blocked |

- **Each request modifies one product.** Unmentioned products are unchanged.
- Success response: `RESP_CODE="201"`.

---

## 6. Contract: Day Close (`Operation: 3`)

### 6.1 Day Close (CMD_CODE 77)

**Host → FDC Request:**
```xml
<?xml version="1.0" encoding="UTF-8"?>
<HOST_REQ>
  <REQ>
    <CMD_CODE>77</CMD_CODE>
    <CMD_NAME>DAY CLOSE</CMD_NAME>
    <CLOSE_IMMEDIATE>{YES|NO}</CLOSE_IMMEDIATE>
    <CLOSE_TIME>{HH:MM:SS}</CLOSE_TIME>
    <TOKEN>{unique_token}</TOKEN>
  </REQ>
  <SIGNATURE>{sha1_hash}</SIGNATURE>
</HOST_REQ>
```

| Field | Description |
|-------|-------------|
| `CLOSE_IMMEDIATE` | `YES` = close now (ignores `CLOSE_TIME`); `NO` = scheduled |
| `CLOSE_TIME` | Time to close (`HH:MM:SS`), valid only within current day |

- Success response: `RESP_CODE="201"`.

---

## 7. Contract: External Authorization (`Operation: Authorize`)

**Port:** The configured External Authorization port (NOT port+1).

**Host → FDC Request:**
```xml
<?xml version="1.0" encoding="utf-8"?>
<FDCMS>
  <AUTH_DATA>
    <PUMP>{pump_number}</PUMP>
    <FP>{filling_point}</FP>
    <AUTH>{TRUE|FALSE}</AUTH>
    <PROD>{product_number}</PROD>
    <PRESET_VOLUME>{liters}</PRESET_VOLUME>
    <PRESET_AMOUNT>{currency_value}</PRESET_AMOUNT>
    <CUSTNAME>{customer_name}</CUSTNAME>
    <CUSTIDTYPE>{1-6}</CUSTIDTYPE>
    <CUSTID>{customer_id}</CUSTID>
    <MOBILENUM>{phone}</MOBILENUM>
    <DISC_VALUE>{discount}</DISC_VALUE>
    <DISC_TYPE>{PERCENT|VALUE}</DISC_TYPE>
    <TOKEN>{0-65535}</TOKEN>
  </AUTH_DATA>
  <FDCSIGNATURE>{sha1_hash}</FDCSIGNATURE>
</FDCMS>
```

### 7.1 Authorization Field Reference

| Field | Required | Type | Description |
|-------|----------|------|-------------|
| `PUMP` | Yes | Integer | RDG/DSB number in FDC for the fuel pump |
| `FP` | Yes | Integer | Filling point within the RDG/DSB |
| `AUTH` | Yes | Boolean | `TRUE` = authorize, `FALSE` = cancel active auth |
| `PROD` | Yes | Integer | Product number; `0` = allow all products |
| `PRESET_VOLUME` | Yes | Decimal | Volume preset in liters/gallons; `0.00` = no volume limit |
| `PRESET_AMOUNT` | Yes | Integer | Amount preset in local currency; `0` = no amount limit |
| `CUSTNAME` | No | String | Customer or company name |
| `CUSTIDTYPE` | No | Integer | Customer ID type (see table below) |
| `CUSTID` | No | String | Unique customer identifier |
| `MOBILENUM` | No | String | Customer phone number |
| `DISC_VALUE` | No | Integer | Discount value; use negative for negative discount |
| `DISC_TYPE` | No | Enum | `PERCENT` = percentage; `VALUE` = money per liter |
| `TOKEN` | No | Integer | 0–65535, included in resulting transaction data |

### 7.2 Customer ID Types

| Type | Description |
|------|-------------|
| 1 | TIN (Tax Identification Number) |
| 2 | Driving License |
| 3 | Voters Number |
| 4 | Passport |
| 5 | NID (National Identity) |
| 6 | NIL (No ID) |

### 7.3 Authorization Response

**FDC → Host Response (success):**
```xml
<?xml version="1.0" encoding="UTF-8"?>
<FDCMS>
    <FDCACK>
        <DATE>{YYYY-MM-DD}</DATE>
        <TIME>{HH:MM:SS}</TIME>
        <ACKCODE>0</ACKCODE>
        <ACKMSG>Success</ACKMSG>
    </FDCACK>
    <FDCSIGNATURE>{sha1_hash}</FDCSIGNATURE>
</FDCMS>
```

**FDC → Host Response (error):**
```xml
<?xml version="1.0" encoding="UTF-8"?>
<FDCMS>
    <FDCACK>
        <DATE>{YYYY-MM-DD}</DATE>
        <TIME>{HH:MM:SS}</TIME>
        <ACKCODE>{error_code}</ACKCODE>
        <ACKMSG>Error</ACKMSG>
    </FDCACK>
    <FDCSIGNATURE>{sha1_hash}</FDCSIGNATURE>
</FDCMS>
```

> **Note:** External Authorization uses `ACKCODE=0` for success (not `201`).

---

## 8. Contract: ATG Data (`Operation: 4`)

### 8.1 Tank Level Data (CMD_CODE 30)

**Host → FDC Request:**
```xml
<?xml version="1.0" encoding="UTF-8"?>
<HOST_REQ>
    <REQ>
        <CMD_CODE>30</CMD_CODE>
        <CMD_NAME>ATG_DATA_REQ</CMD_NAME>
        <TOKEN>{unique_token}</TOKEN>
    </REQ>
    <SIGNATURE>{sha1_hash}</SIGNATURE>
</HOST_REQ>
```

**FDC → Host Response:**
```xml
<?xml version="1.0" encoding="UTF-8"?>
<FDC_RESP>
  <TABLE VERSION="1.0">
    <ANS RESP_CODE="201" RESP_MSG="SUCCESS" TOKEN="{token}" />
    <VR_INV_AL_INFO DATE="{YYYY-MM-DD}" TIME="{HH:MM:SS}" />
    <TANK_DATA>
      <TANK ID="{n}" PROD_NUM="{n}" VOL="{liters}" TC_VOL="{tc_liters}"
            ULLAGE="{liters}" HEIGHT="{cm}" WATER="{cm}"
            TEMP="{±degrees}" WATER_VOL="{liters}"
            AL_MASK="{bitmask}" AL_NUM="{n}" />
      <!-- one TANK element per tank -->
    </TANK_DATA>
  </TABLE>
  <SIGNATURE>{sha1_hash}</SIGNATURE>
</FDC_RESP>
```

#### Tank Data Model

| Attribute | Type | Description |
|-----------|------|-------------|
| `ID` | Integer | Tank index (0-based) |
| `PROD_NUM` | Integer | Product number assigned to tank |
| `VOL` | Decimal | Gross volume |
| `TC_VOL` | Decimal | Temperature-compensated volume |
| `ULLAGE` | Decimal | Available capacity |
| `HEIGHT` | Decimal | Product height |
| `WATER` | Decimal | Water level height |
| `TEMP` | String | Temperature with sign (e.g. `"+11.9"`) |
| `WATER_VOL` | Decimal | Water volume |
| `AL_MASK` | String | Alarm bitmask |
| `AL_NUM` | Integer | Number of active alarms |

### 8.2 ATG Deliveries (CMD_CODE 35)

**Host → FDC Request:**
```xml
<?xml version="1.0" encoding="UTF-8"?>
<HOST_REQ>
    <REQ>
        <CMD_CODE>35</CMD_CODE>
        <CMD_NAME>ATG_DEL_REQ</CMD_NAME>
        <TOKEN>{unique_token}</TOKEN>
    </REQ>
    <SIGNATURE>{sha1_hash}</SIGNATURE>
</HOST_REQ>
```

- Returns `RESP_CODE="201"` with `<DELIVERY>` element when available.
- Returns `RESP_CODE="205"` with `RESP_MSG="NO DEL AVAILABLE"` when none.
- Host must ACK with `CMD_CODE=201` after successful receipt.
- **On error:** host does NOT respond; FDC retries after timeout.

---

## 9. Contract: CSR Data (`Operation: 5`)

### 9.1 CSR Data Request (CMD_CODE 40)

**Host → FDC Request:**
```xml
<?xml version="1.0" encoding="UTF-8"?>
<HOST_REQ>
    <REQ>
        <CMD_CODE>40</CMD_CODE>
        <CMD_NAME>CSR_DATA_REQ</CMD_NAME>
        <TOKEN>{unique_token}</TOKEN>
    </REQ>
    <SIGNATURE>{sha1_hash}</SIGNATURE>
</HOST_REQ>
```

**FDC → Host Response (data available):**
```xml
<FDC_RESP>
  <TABLE VERSION="1.0">
    <ANS RESP_CODE="201" RESP_MSG="SUCCESS" TOKEN="{token}" />
    <CSR>{binary_ascii_data}</CSR>
  </TABLE>
  <SIGNATURE>{sha1_hash}</SIGNATURE>
</FDC_RESP>
```

- CSR data is binary information in ASCII format, same as submitted by VFD to COLLECT.
- DSB/RDG count: **16** (0–15) for this API (vs. 12 for VFD/FDC1).
- Returns `RESP_CODE="205"` with `RESP_MSG="NO NEW CSR AVAILABLE"` when none.
- Host must ACK with `CMD_CODE=201` after successful receipt.
- **On error:** host does NOT respond; FDC retries.

---

## 10. Error Handling Summary

| Scenario | Behavior |
|----------|----------|
| Signature mismatch | FDC returns `251` error |
| Bad XML format | FDC returns `255` error |
| Bad header | FDC returns `256` error |
| Invalid token | FDC returns `253` error |
| Wrong transaction mode | FDC returns `206` error |
| Pump not ready | FDC returns `258` error |
| DSB offline | FDC returns `260` error |
| Host receives corrupt data | Host does NOT respond; FDC retries after timeout |
| FDC receives corrupt data | FDC returns appropriate error code |

### Token Rules

- Max 16 decimal digits (Operations 1–5).
- Range 0–65535 (External Authorization).
- Each token should be unique per communication sequence.
- Used to correlate request/response pairs.

---

## 11. Integration Checklist for Edge Agent

- [ ] Implement SHA1 signature calculation (REQ-based and AUTH_DATA-based)
- [ ] Handle two ports: auth port (configured) and management port (configured + 1)
- [ ] Support both ON_DEMAND (polling) and UNSOLICITED (push) transaction modes
- [ ] Parse all `<TRN>`, `<RFID_CARD>`, `<DISCOUNT>`, `<CUST_DATA>` attributes
- [ ] Implement product/price read (CMD 55) and write (CMD 66)
- [ ] Implement day close (CMD 77) with immediate and scheduled modes
- [ ] Implement external authorization with preset volume/amount
- [ ] Handle authorization cancel (`AUTH=FALSE`)
- [ ] Implement ATG tank-level polling (CMD 30)
- [ ] Implement ATG delivery polling (CMD 35)
- [ ] Implement CSR data polling (CMD 40)
- [ ] ACK all successfully received transactions/deliveries/CSR with CMD_CODE 201
- [ ] Handle "no data available" (205) responses gracefully
- [ ] Handle all error codes (206, 207, 251–260)
- [ ] On host-side errors for unsolicited data, do NOT respond (FDC auto-retries)
- [ ] Generate unique tokens per request sequence
