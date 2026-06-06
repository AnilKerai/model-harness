---
name: debtor-verification
description: Validate and verify debtors found on customer accounts. Use this skill whenever you need to check if a debtor is legitimate by verifying company details, contact information, and registration records. Flags suspicious patterns and confirms authenticity.
when_to_use: When asked to verify a debtor — confirm company identity, registration, and contact authenticity before any funding decision.
version: 1.0.0
---

# Debtor Verification

You help a credit controller perform an initial debtor verification check before proceeding with any potential funding.
- Company identity matches across multiple sources
- Contact information is authentic and consistent
- Company registration is genuine
- No obvious red flags or mismatches

## Required Tools

- `submit_query` / `fetch_query_results` — internal client database (async: submit then poll until status is "ready")
- `web_search` — search the public web
- `web_fetch` — fetch a web page by URL; only call after `web_search` has returned the URL — do not guess URLs

## Validation Workflow

For each debtor, gather the following information and run through these checks in order.

### Information Gathering

**Choosing the `query_id`:**

- If the user supplies a **company name** (e.g. "verify Acme Corporation Ltd"), call `submit_query` using `query_id` of `get_client_by_registered_name` with the `client_registered_name` argument.
- If the user supplies a **numeric or alphanumeric identifier** — including a Companies House registration number (e.g. "07198981") or any other reference code — call `submit_query` using `query_id` of `get_client_by_registration_number` with the `client_registration_number` argument.

Poll `fetch_query_results` with the returned handle until status is `ready`. Expected fields: AGENCY_NAME, CLIENT_ID, CLIENT_REGISTERED_NAME, CLIENT_REGISTRATION_NUMBER, CLIENT_COUNTRY_JURISDICTION, CLIENT_ORGANISATION_TYPE.

If multiple client rows are returned, **ask** the user which client to use.

**Retrieving Client Contacts:**

Once you have a CLIENT_ID, call `submit_query` using `query_id` of `get_client_contacts_by_client_id`. Poll until ready. Expected additional fields: CONTACT_NAME, CONTACT_EMAIL, CONTACT_TELEPHONE, COMPANY_WEBSITE.

### Check 1: Company Website Domain vs. Primary Contact Email

**Purpose**: Verify the primary contact is actually associated with the company.

1. Extract the domain from the company website (e.g., www.company.com → company.com)
2. Extract the domain from the primary contact email (e.g., ap@company.com → company.com)
3. Compare these domains

**Results**:

- **PASS**: Domains match exactly
- **FAIL**: Domains don't match — red flag: contact isn't using company email address
- **INCONCLUSIVE**: No website could be found via web_search; or close match but not exact (different country suffix, subsidiary)

### Check 2: AP/Finance Contact Email Validation

**Purpose**: Confirm the AP/Finance contact is authentic, not a sales or personal email.

**Expected patterns for PASS**:
- invoices@company.com, ap@company.com, accounts-payable@company.com, finance@company.com, and similar

**Results**:

- **PASS**: Email matches known AP/Finance patterns
- **FAIL**: Email is sales-focused or a personal address — hard red flag
- **INCONCLUSIVE**: Unable to determine function from local-part alone

### Check 3: Company Registration Verification

**Purpose**: Confirm the registration number exists and is active in Companies House. Name matching is handled separately by Check 4.

1. Search the UK Companies House database (or equivalent if non-UK) using the registration number
2. Confirm whether an active record exists for that number

**Results**:

- **PASS**: A record is found for the registration number and the company is active
- **FAIL**: Registration number does not exist in Companies House — hard red flag
- **INCONCLUSIVE**: No registration number available, or Companies House could not be reached

### Check 4: Web Research for Supporting Information

While validating, gather supporting information:

- **Finding the company website**: Always use `web_search` to locate the official website URL first. Only call `web_fetch` after `web_search` has returned the URL.
- **Trading names/aliases**: Search the company website for alternative trading names
- **Companies House name match**: Check whether the name on the Companies House record matches CLIENT_REGISTERED_NAME (fuzzy match acceptable)

**Results**:

- **PASS**: Companies House name matches CLIENT_REGISTERED_NAME (exact or near-exact)
- **FAIL**: Material mismatch between Companies House name and debtor record
- **INCONCLUSIVE**: Could not retrieve Companies House record, or name is ambiguous

### Check 5: Telephone Number

**Purpose**: Assess whether a telephone number is present and appears to be a legitimate business number.

1. Check the debtor record for a telephone number on the AP/Finance contact
2. If no number is present, mark as **INCONCLUSIVE** — absence alone is not a red flag

**Results**:

- **PASS**: A number is present and appears to be a valid UK landline (01xxx, 02xxx, 03xxx prefix)
- **INCONCLUSIVE**: No number present, or the number is a mobile (07xxx)
- **FAIL**: The number is obviously invalid or cannot be a real business number

### Check 6: Registered Company Name on Website (Experimental)

**Purpose**: Cross-reference the registered company name published on the debtor's official website against CLIENT_REGISTERED_NAME.

From the `web_fetch` content, look for any mention of a registered company name (e.g. "Registered in England as…", footer registration notices).

**Results**:

- **PASS**: A registered name is explicitly stated on the website and matches CLIENT_REGISTERED_NAME (exact or near-exact)
- **FAIL**: A registered name is explicitly stated but differs materially
- **INCONCLUSIVE**: No registered name found on the fetched page, or website was not fetched

### Final Classification

After running all checks, assign an overall status:

**PASS (Verified Legitimate)**: Company domain matches contact email; AP/Finance contact validated; registration confirmed; no red flags.

**FAIL (Likely Fraudulent or Suspicious)**: Hard red flags present — mismatched domains, sales email as AP contact, invalid registration, or multiple checks failed.

**INCONCLUSIVE (Uncertain/Needs Manual Verification)**: Some checks passed, others inconclusive. Recommend human follow-up.

## Edge Cases & Notes

- **No website provided**: Mark as REVIEW REQUIRED
- **Non-UK company**: Apply same logic but search appropriate company registration database (OpenCorps, etc.)
- **Mobile number as AP contact**: Mark telephone check as Inconclusive — unusual but not a hard red flag
- **Abbreviations in company name**: Note as inconclusive, not failure
- **Multiple valid AP emails found**: PASS if at least one follows expected pattern
- **No AP email found**: Not an automatic fail — move to REVIEW REQUIRED if all other checks pass

## Confidence Scoring

Use one of these values in the **Confidence** column: `High`, `Medium`, `Low`.

Choose confidence based on the quality and completeness of the evidence you actually have.

## Output Format

**Exception — debtor not found**: If the lookup returns no record or an error, respond with a short prose message explaining that the debtor could not be found and asking the user to check the name and try again. Do not render tables.

Otherwise render exactly two markdown tables, in this order:

1. A **Checks** table with columns: **Check**, **Result**, **Confidence**, **Brief reasoning**
2. A **Supporting links** table with columns: **Link type**, **URL**, **Notes**

Allowed result values: 🟢 `Pass`, 🔴 `Fail`, 🟡 `Inconclusive`

The checks table must contain exactly seven data rows:

| Check | Result | Confidence | Brief reasoning |
|---|---|---|---|
| Company web address matches contact email | <Pass\|Fail\|Inconclusive> | <High\|Medium\|Low> | <brief evidence-based reason> |
| AP/Finance contact email matches company name (fuzzy) | <Pass\|Fail\|Inconclusive> | <High\|Medium\|Low> | <brief evidence-based reason> |
| Company Registration Number is authentic (Companies House) | <Pass\|Fail\|Inconclusive> | <High\|Medium\|Low> | <brief evidence-based reason> |
| Companies House company name matches debtor name in full (fuzzy) | <Pass\|Fail\|Inconclusive> | <High\|Medium\|Low> | <brief evidence-based reason> |
| Telephone number is authentic/operational | <Pass\|Fail\|Inconclusive> | <High\|Medium\|Low> | <brief evidence-based reason> |
| [Experimental] Registered company name on debtor website matches records | <Pass\|Fail\|Inconclusive> | <High\|Medium\|Low> | <brief evidence-based reason> |
| Concerns requiring further investigation | <brief note, or "None"> | — | — |

| Link type | URL | Notes |
|---|---|---|
| Directors details | <url\|inconclusive> | <brief note> |
| Financial activity | <url\|inconclusive> | <brief note> |

**Output only the two tables — no prose, no summary, no headings before or after them.**

For the **Concerns** row: write a brief note about anything worth a credit controller's attention not already captured by checks 1–6 (e.g. very recently incorporated, director with multiple dissolved companies). Write **None** if nothing additional to flag. Do not use Pass/Fail/Inconclusive — it is a note, not a verdict. Inconclusive checks already recorded in their rows do not constitute additional concerns.
