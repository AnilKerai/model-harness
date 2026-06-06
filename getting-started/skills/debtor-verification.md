---
name: debtor-verification
description: Validate and verify debtors found on customer accounts. Use this skill whenever you need to check if a debtor is legitimate by verifying company details, contact information, and registration records. Flags suspicious patterns and confirms authenticity.
when_to_use: When asked to verify a debtor — confirm company identity, registration, and contact authenticity before any funding decision.
version: 2.0.0
---

# Debtor Verification

You help a credit controller perform an initial debtor verification check before proceeding with any potential funding.

## Required Tools

**Data retrieval:**
- `submit_query` / `fetch_query_results` — internal client database (async: submit then poll until status is "ready")
- `web_search` — search the public web
- `web_fetch` — fetch a web page by URL; only call after `web_search` has returned the URL — do not guess URLs

**Deterministic checks — call these tools and use their returned verdict directly. Do not override or reinterpret the result:**
- `check_email_domain_match` — Check 1: domain from website URL vs domain from contact email
- `check_ap_email_pattern` — Check 2: whether the email local-part is a recognised AP/finance pattern
- `check_company_name_match` — Checks 4 & 6: normalised fuzzy name comparison
- `check_phone_format` — Check 5: phone number format validation for the company's jurisdiction

## Validation Workflow

### Information Gathering

**Choosing the `query_id`:**

- If the user supplies a **company name** (e.g. "verify Acme Corporation Ltd"), call `submit_query` using `query_id` of `get_client_by_registered_name` with the `client_registered_name` argument.
- If the user supplies a **numeric or alphanumeric identifier** — including a Companies House registration number (e.g. "07198981") or any other reference code — call `submit_query` using `query_id` of `get_client_by_registration_number` with the `client_registration_number` argument.

Poll `fetch_query_results` with the returned handle until status is `ready`. Expected fields: AGENCY_NAME, CLIENT_ID, CLIENT_REGISTERED_NAME, CLIENT_REGISTRATION_NUMBER, CLIENT_COUNTRY_JURISDICTION, CLIENT_ORGANISATION_TYPE.

If multiple client rows are returned, **ask** the user which client to use.

**Retrieving Client Contacts:**

Once you have a CLIENT_ID, call `submit_query` using `query_id` of `get_client_contacts_by_client_id`. Poll until ready. Expected additional fields: CONTACT_NAME, CONTACT_EMAIL, CONTACT_TELEPHONE, COMPANY_WEBSITE.

### Check 1: Company Website Domain vs. Primary Contact Email

Call `check_email_domain_match` with:
- `website_url`: the COMPANY_WEBSITE from the contacts record
- `contact_email`: the CONTACT_EMAIL from the contacts record

Use the `result`, `confidence`, and `reason` returned by the tool directly in the checks table row.

### Check 2: AP/Finance Contact Email Validation

Call `check_ap_email_pattern` with:
- `email`: the CONTACT_EMAIL from the contacts record

Use the `result`, `confidence`, and `reason` returned by the tool directly in the checks table row.

### Check 3: Company Registration Verification

Search Companies House (or the equivalent register for non-UK companies) to confirm the registration number exists and the company is active.

1. Use `web_search` to find the Companies House record for the CLIENT_REGISTRATION_NUMBER
2. Use `web_fetch` to retrieve the Companies House company overview page
3. Confirm whether an active record exists for that number

**Results**:

- **PASS**: A record is found and the company status is Active
- **FAIL**: Registration number does not exist — hard red flag
- **INCONCLUSIVE**: No registration number available, or Companies House could not be reached

### Check 4: Companies House Company Name vs. Debtor Record

After fetching the Companies House overview page (in Check 3), call `check_company_name_match` with:
- `name_a`: CLIENT_REGISTERED_NAME from the internal database record
- `name_b`: the company name as shown on the Companies House page

Use the `result`, `confidence`, and `reason` returned by the tool directly in the checks table row.

### Check 5: Telephone Number is a Valid UK Landline

**Purpose**: Format check only — no web research required. If a telephone number is on file, confirm it is a valid UK landline. If no number is on file, mark Inconclusive.

Call `check_phone_format` with:
- `phone_number`: the CONTACT_TELEPHONE from the contacts record
- `jurisdiction`: the CLIENT_COUNTRY_JURISDICTION from the database record

Use the `result`, `confidence`, and `reason` returned by the tool directly in the checks table row.

### Check 6: Registered Company Name on Website (Experimental)

Fetch the company's website (using `web_search` to find it, then `web_fetch`). Look for any explicit mention of a registered company name (e.g. "Registered in England as…", footer registration notices).

If a registered name is found, call `check_company_name_match` with:
- `name_a`: CLIENT_REGISTERED_NAME from the database record
- `name_b`: the registered name as stated on the website

Use the `result`, `confidence`, and `reason` returned by the tool directly in the checks table row.

If no registered name is found on the fetched page, record **INCONCLUSIVE** with reason "No registered company name found on the fetched page."

### Final Classification

After running all checks, assign an overall status based on the verdicts:

**PASS (Verified Legitimate)**: Company domain matches contact email; AP/Finance contact validated; registration confirmed; no red flags.

**FAIL (Likely Fraudulent or Suspicious)**: Hard red flags present — mismatched domains, sales email as AP contact, invalid registration, or multiple checks failed.

**INCONCLUSIVE (Uncertain/Needs Manual Verification)**: Some checks passed, others inconclusive. Recommend human follow-up.

## Edge Cases & Notes

- **No website provided**: Mark Check 1 as INCONCLUSIVE — no website to compare against
- **Non-UK company**: Apply same logic but search appropriate company registration database (OpenCorps, etc.)
- **Mobile number as AP contact**: `check_phone_format` will return Fail — AP contact numbers should be landlines
- **Abbreviations in company name**: `check_company_name_match` handles normalisation; use its verdict
- **Multiple valid AP emails found**: PASS if at least one follows expected pattern
- **No AP email found**: Not an automatic fail — move to REVIEW REQUIRED if all other checks pass

## Confidence Scoring

Use one of these values in the **Confidence** column: `High`, `Medium`, `Low`.

For checks using deterministic tools, use the confidence returned by the tool. For checks requiring your own judgement (Check 3), choose based on the quality and completeness of the evidence you actually have.

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
| Telephone number is a valid UK landline | <Pass\|Fail\|Inconclusive> | <High\|Medium\|Low> | <brief evidence-based reason> |
| [Experimental] Registered company name on debtor website matches records | <Pass\|Fail\|Inconclusive> | <High\|Medium\|Low> | <brief evidence-based reason> |
| Concerns requiring further investigation | <brief note, or "None"> | — | — |

| Link type | URL | Notes |
|---|---|---|
| Directors details | <url\|inconclusive> | <brief note> |
| Financial activity | <url\|inconclusive> | <brief note> |

**Output only the two tables — no prose, no summary, no headings before or after them.**

For the **Concerns** row: write a brief note about anything worth a credit controller's attention not already captured by checks 1–6 (e.g. very recently incorporated, director with multiple dissolved companies). Write **None** if nothing additional to flag. Do not use Pass/Fail/Inconclusive — it is a note, not a verdict. Inconclusive checks already recorded in their rows do not constitute additional concerns.
