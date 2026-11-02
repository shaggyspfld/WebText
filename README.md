# WebText (ASP.NET Core 10 + Telnyx or VoIP.ms)

WebText is a web app that lets everyone in an office text its clients through one Telnyx or VoIP.ms number, with a
shared, threaded history of every conversation. It isn't tied to one kind of business. Each customer gets their own
**installation** with its own name, chosen when the installer runs: for example *TaxText* for an accounting firm,
*BillWatch* for a legislative-monitoring service, or *RepairText* for a computer repair shop. See
[Installations](#installations-one-per-customer) below.

## Installations (one per customer)

`tools\Install-Server.ps1` asks for the **application name** of the installation it's setting up, or you can pass
`-AppName TaxText`. That name becomes:

| Used for | Example for `-AppName TaxText` |
|---|---|
| IIS site and app pool | `TaxText` |
| Install folder | `C:\websites\taxtext` |
| Database (default) | `TaxText` |
| Name in the app's top bar (until changed under Settings → General → Office name) | TaxText |
| Backup file names | `taxtext-backup-2026-11-02_2300.wtbackup` |
| Session cookie and encryption keys | `TaxText.Session`, `TaxText.*` |

- **Use the same name every time you update that installation.** It keys the encryption of the saved API keys and
  authenticator secrets. The installer refuses to update a folder that belongs to a differently named
  installation.
- **Renaming an installation:** make a backup, install under the new name, and restore the backup there.
- **Several installations on one server** work. Give each its own `-AppName`, `-HostName` (DNS name) and database.
  - They share port 443 using host names.
  - One Cloudflare tunnel can serve all of them: add a published route per installation with its own public webhook
    host name.
  - Each installation has its own Telnyx/VoIP.ms account and number, users, clients and settings.
- **The code, this README and the scripts** call the product WebText; only the installed copy carries the
  customer's name. Starter templates and examples are deliberately generic. Each customer's admin replaces them with
  their own on the Templates and Auto-replies pages.

**Updating an installation made before the WebText rename** (the earlier TaxText build): run the new installer with
`-AppName TaxText`, the name it already had.
- It reuses the same site, folder and database, and its saved keys stay readable.
- It moves the database connection in `appsettings.json` to the new format by itself.
- Backup files made by the earlier build (`.ttbackup`) can't be restored by WebText, so make a fresh backup after
  updating.

## What it does

- **Shared inbox.** Conversations are listed newest first with unread counts. Each client's texts appear as one
  thread, like a phone's messaging app. Search works on name, phone, account number or message text.
- **Who sent what.** Every outbound text and internal note is stamped with the sender's Windows login
  (`DOMAIN\user`). There's nothing extra to sign in to.
- **Incoming texts.** Telnyx posts each reply to a webhook. The app files it in the matching client's thread and
  pushes it live to every open browser.
  - Unknown numbers get an auto-created "Unknown" client. You can name it, or merge it into an existing client.
  - Spouses can share a number. A reply is filed under whichever of them was texted most recently.
- **Delivery status.** Each sent text shows queued → sent → delivered (or the carrier's error), updated live.
  Failed texts have a Resend button.
- **Internal notes / call log.** Add notes to the thread ("called, left voicemail") that are never sent to the client.
  They're useful when a client calls in or for follow-up.
- **Multi-user.** Updates are pushed in real time over SignalR.
  - The thread header shows who else is viewing it.
  - A "Jane is typing a reply…" hint appears so two people don't answer the same client.
- **Templates and merge fields.** Six general-purpose starter templates are included; replace them with your own. Merge fields are `{FirstName}`, `{LastName}`
  and `{FullName}`.
- **Bulk send.** Paste a list of account numbers or phone numbers, or pick everyone.
  - Preview the recipient list before sending.
  - Texts are queued and sent at a safe rate.
  - Each text lands in that client's own thread.
- **Opt-out handling.** A client who replies STOP / UNSUBSCRIBE / CANCEL… is flagged, and further texts are blocked.
  Replying START re-enables texts.
- **Keyword auto-replies** (**Auto-replies**). When a client texts a keyword such as `HOURS` or `DOCS`, the app
  sends a set reply straight away, at any hour. See [Auto-replies](#auto-replies) below.
- **Message log.** Filter by staff member, date, direction or status, and export to CSV for audits.
- **Client management.** Add, edit, merge and delete clients. Filter by list, opt-out status, unknown numbers or
  never-texted.
- **Client import and export.** Import a CSV exported from your CRM, accounting or practice-management software. Export clients to CSV, either all or
  just the current search/filter.
  - The export's first five columns match the import format.
  - So you can fix names and numbers in Excel and import the file back to update them in bulk.
- **Recipient lists.** Saved groups such as "2026 extensions" or "Business clients", under **Lists**.
  - Add clients one at a time, by pasting a column of account numbers or phone numbers, or by ticking them on the
    Clients page.
  - Each list exports to CSV, and Bulk send can text a whole list.
  - Removing a client from a list, or deleting a list, never deletes the client or their texts.
- **Usage reports** (**Reports**), for any date range or preset (last 30 days, this month, this quarter, year to date…):
  - Headline numbers: texts sent and received, delivery rate, client reply rate, median time to respond to a client,
    texts still awaiting a reply, active staff, opt-outs, and SMS segments (with an estimated cost if configured).
  - Activity per day, or per week for long ranges.
  - **By Windows user:** texts sent (one-to-one vs bulk), delivered, failed, clients texted, notes, client replies
    they handled, and their median response time.
  - **By client / phone number:** sent, received, notes, failures, which staff were involved, and last activity.
    The table can be sorted.
  - Every table exports to CSV.
- **Windows or app-hosted sign-in.** Staff sign in automatically with Windows/Active Directory, or (for hosting
  outside your network) with app accounts: password plus an authenticator-app code, with a full security log.
  See [Sign-in](#sign-in-windows-or-local-accounts).
- **Access by group.** Only members of **`txtAppUser`** can open the app at all, and only members of
  **`txtAppAdmin`** can administer it.
  - Admins don't also need to be in `txtAppUser`.
  - Anyone else who signs in gets an "Access denied" page naming the group they need.
  - Admin-only: the Settings page, Reports, editing templates, and deleting clients.
- **Configuration in the app, not in files.** Admins set everything on the **Settings** page, and it is stored in SQL.
  This covers the Telnyx API key and number, the webhook public key, send rate, the two AD group names, office name,
  time zone and the cost estimate.
  - The API key is encrypted in the database.
  - Every change is logged with who made it.
  - Changes apply within seconds, with no restart.
  - `appsettings.json` holds only the database connection.

## How it fits together

```
 Staff browsers ──(Windows auth, HTTPS)──► IIS: WebText.Web ──► SQL Server
       ▲   live updates (SignalR)              │   ▲
       └───────────────────────────────────────┘   │ POST /api/webhooks/telnyx
                         │ POST /v2/messages        │ (inbound texts + delivery status,
                         ▼                          │  Ed25519-signed)
                      Telnyx  ──────────────────────┘
```

- **Sending.** Every text — single or bulk — is saved as `pending` first. A background worker sends pending texts to
  Telnyx one at a time at the configured rate.
  - A slow API call never freezes anyone's screen.
  - Nothing is lost if the app restarts mid-send.
- **Receiving.** Telnyx calls `/api/webhooks/telnyx`. The app checks the Ed25519 signature and stores the raw event.
  - Events are de-duplicated by event id, because Telnyx retries.
  - The app then files the message or updates its delivery status.
- **Data.** Client and message data is in seven tables: `Clients`, `Messages`, `MessageMedia`, `MessageTemplates`,
  `WebhookEvents`, `ClientGroups` and `ClientGroupMembers`.
  - Configuration is in `AppSettings`, with its change log in `AppSettingChanges`.
  - The encryption key ring is in `DataProtectionKeys`.
  Times are stored in UTC and shown in local time.

```
src/WebText.Web/
  Program.cs                      startup: auth, AD-group policies, EF Core, SignalR, worker
  Auth/GroupAccess.cs             txtAppUser / txtAppAdmin membership checks (AD or local groups)
  Auth/AuthSetup.cs               Windows or Local sign-in, chosen on the Settings page
  Auth/LocalAccounts.cs           local users: passwords, lockout, authenticator codes, security log
  Auth/Totp.cs                    authenticator codes (RFC 6238)
  Services/Settings.cs            setting definitions, DB-backed settings cache, encrypted secrets
  Pages/Admin/Settings.cshtml     the admin Settings page
  Data/                           entities + DbContext + seed templates
  Services/TelnyxClient.cs        Telnyx send API
  Services/VoipMsClient.cs        VoIP.ms send API (sendSMS / sendMMS)
  Services/SmsProviders.cs        picks the provider chosen on the Settings page
  Api/VoipMsWebhookEndpoint.cs    VoIP.ms incoming-text callback
  Services/TelnyxSignatureVerifier.cs
  Services/MessagingService.cs    queueing, webhooks, inbound threading, opt-outs
  Services/OutboundMessageWorker.cs
  Hubs/MessagingHub.cs            live updates + who's viewing/typing
  Api/                            JSON endpoints for the inbox + the Telnyx webhook
  Services/ClientLookup.cs        shared client search, pasted-list matching, client CSV export
  Services/ReportService.cs       usage statistics
  Pages/                          Inbox, Clients (+Edit, Import), Groups (lists), Bulk, Templates, Log, Reports
  wwwroot/                        CSS, JS, bundled SignalR client (no CDN needed)
sql/create-database.sql           optional hand-run schema script
sql/upgrade-001-recipient-lists.sql      adds the list tables to a database created by the first version
sql/upgrade-002-settings-in-database.sql adds the settings tables to a database created by an earlier version
sql/upgrade-003-auto-replies.sql         adds auto-replies and their change history
sql/upgrade-004-local-sign-in.sql        adds local users, groups and the security log
sql/upgrade-005-scheduled-backups.sql    adds the backup run history
sql/reset-access-groups.sql              emergency: put the AD group names back to txtAppUser / txtAppAdmin
sql/reset-sign-in-to-windows.sql         emergency: switch from Local sign-in back to Windows
```

---

## 1. Texting provider setup

Choose the provider on **Settings → Texting provider**. The page then shows only that provider's settings. You can
switch later: earlier texts stay in each client's history, and new texts go through the newly selected provider.

| | Telnyx | VoIP.ms |
|---|---|---|
| Texts over 160 characters | sent as one long SMS | one MMS (default) or split into numbered texts |
| Delivery status (delivered / failed at the carrier) | yes | no: texts show as "sent" once VoIP.ms accepts them |
| Incoming webhook security | signed by Telnyx + IP allowlist | secret key in the callback URL (+ optional IP allowlist) |
| Sending limit | your 10DLC campaign's rate | 100 texts/day through the API unless VoIP.ms raises it |
| STOP handling | Telnyx and the app | the app only |
| Countries | many | US and Canada only |

### Business texting registration (10DLC): start this first

US carriers block business texts from ordinary 10-digit numbers ("10DLC") unless the business and its use are
registered with **The Campaign Registry (TCR)**. Registration goes through your provider. Without it, texts are
silently dropped or rejected: Telnyx reports error **40010 "Not 10DLC registered"**. It applies to Telnyx and VoIP.ms
alike, and to US local numbers. (Toll-free numbers use *Toll-Free Verification* instead; see the end of this section.)
Approval takes from a few days to two weeks or more, and rejections add more, so **apply before go-live**.

There are two registrations:
1. A **Brand**: your firm.
2. A **Campaign**: what you'll text, to whom, and how they agreed.

Your number(s) are then attached to the approved campaign.

#### What you need before applying

**For the Brand**
- **Legal business name and EIN, exactly as the IRS has them.** Copy them from the IRS's EIN letter (CP 575 or 147C). A mismatch
  is the most common reason for "Brand not verified". Sole proprietors without an EIN register as a *Sole
  Proprietor* brand, which has lower limits.
- **The DBA / brand name clients know you by,** your business address, a contact email and phone, and the industry
  (Professional Services / Accounting).
- **A working public website** on your own domain that matches the brand name. Reviewers check it.

**On that website**
- **A Privacy Policy page.** It must be your firm's own, not a generic template, and must contain an SMS clause such as:
  > *Text messaging: We collect your mobile number to send you text messages about your account and our services.
  > No mobile information will be shared with third parties or affiliates for marketing or promotional purposes.
  > Text messaging opt-in data and consent will not be shared with any third parties.*
- **SMS Terms (a Terms page or a section of it).** Include:
  - the program name (e.g. "Example Co. client text updates");
  - what you send;
  - "Message frequency varies";
  - "Message and data rates may apply";
  - "Reply HELP for help, STOP to cancel";
  - your phone or email for support;
  - a link to the Privacy Policy.

**How clients opt in (the "message flow" / call to action).** Reviewers want to see exactly where and how a client
agrees, with the required disclosures at that point. The usual place is the **engagement letter, work order, sign-up
form or client intake form** (paper, web or e-signature), with a separate, unticked checkbox:

> ☐ *I agree to receive text messages from Example Co. at the mobile number above about my account, appointments
> and service updates. Message frequency varies. Message and data rates may apply. Reply HELP for help or STOP to
> opt out at any time. Consent is not a condition of service. See our Privacy Policy and SMS Terms at
> example.com/privacy and example.com/sms-terms.*

Describe that in the campaign, e.g. *"Clients opt in by ticking an SMS consent checkbox on our engagement
letter / work order / sign-up form when they provide their mobile number; the form text is: …"*. Attach or link a
copy of the form if the portal allows. Verbal consent over the phone is weaker and gets more scrutiny. SMS consent must
be separate from email or phone consent.

#### The campaign

- **Use case.**
  - *Mixed*, or *Low Volume Mixed* for small senders (much cheaper per month, with a low daily cap; check the current
    limit in the portal), covering account notifications plus customer care.
  - Choose *Account Notification* or *Customer Care* if that's all you'll send.
  - Don't choose Marketing unless you'll send promotions; that needs stricter consent.
- **Description** (40+ characters, consistent with your website) and **sample messages** (2–5; brand name in each,
  matching your real templates, opt-out language in at least the first). Examples for three kinds of customer:

  | | Description | Sample messages |
  |---|---|---|
  | **Accounting firm** | *Example CPA texts existing clients who opted in about their own tax returns: documents still needed, appointment reminders, filing status and deadline reminders, and replies to client questions. No marketing.* | *Example CPA: Hi Ann, we still need your 1099-B to finish your return. Reply here with questions. Reply STOP to opt out.* / *Example CPA: Your federal return was accepted on 3/14. Questions? Call (312) 555-0100.* |
  | **Legislative monitoring** | *Example Policy Watch texts subscribers who opted in when a bill they follow is introduced, amended, scheduled for hearing or voted on, and answers subscriber questions. No marketing.* | *Example Policy Watch: HB 1234 (data privacy) has a committee hearing Tue 2/10 at 9 am. Reply STOP to opt out.* / *Example Policy Watch: SB 77 passed the Senate 31-18 today. Reply for details.* |
  | **Computer repair** | *Example Tech Repair texts customers who opted in on their work order about their repair: diagnosis and quote, approval questions, parts status and pickup notices, and replies to their questions. No marketing.* | *Example Tech Repair: Hi Sam, your laptop is diagnosed: new SSD + reinstall, $189. Reply OK to approve. Reply STOP to opt out.* / *Example Tech Repair: Your PC is ready for pickup. We're open 9-6 Mon-Sat.* |

- **Campaign attributes.**
  - Subscriber opt-in: **yes**; opt-out: **yes**; help: **yes**.
  - Embedded links: only if your templates contain them. Public link shorteners such as bit.ly get messages blocked.
  - Embedded phone numbers: yes, if you include your office number.
  - Age-gated, lending, direct lending: no.
- **Keywords and auto-replies.** The forms ask for these; use the same wording you'll actually send.
  - Opt-out keywords: **STOP, END, CANCEL, UNSUBSCRIBE, QUIT, STOPALL, REVOKE, OPTOUT.** This app honours all of
    them: the client is flagged as opted out and further texts are blocked.
  - Opt-out confirmation: *"Example Co.: You're unsubscribed and won't receive more texts. Reply START to resubscribe."*
  - Help keyword **HELP**, reply: *"Example Co.: For help call (312) 555-0100 or email info@example.com. Msg & data rates
    may apply. Reply STOP to opt out."*
  - Opt-in keyword **START** (this app re-enables texts on START), confirmation: *"Example Co.: You're subscribed to text
    updates about your account. Msg frequency varies. Msg & data rates may apply. Reply HELP for help, STOP to opt
    out."*
- **Keyword auto-replies** you set up in this app (e.g. HOURS, DOCS) should also be mentioned in the description:
  *"Clients can text keywords such as HOURS or DOCS to get an automatic reply."*

**Common rejections:**
- the EIN or legal name doesn't match the IRS;
- the website doesn't match the brand, or has no SMS privacy clause;
- the opt-in isn't described or lacks the disclosures;
- the sample messages don't match the description or the brand;
- the description and the use case disagree.

Fix exactly what the rejection says and resubmit. Each carrier review costs a fee.

#### Telnyx 10DLC

Portal → **Messaging → 10DLC**.
1. **Brands → Create Brand** with the details above. EIN-verified brands are checked automatically, usually within
   minutes to a day.
2. **Campaigns → Create Campaign** for that brand: use case, description, message flow, sample messages, keywords and
   replies, and the Privacy Policy / Terms URLs. Telnyx reviews it first, then sends it to the carriers. That typically
   takes about 3 business days after Telnyx's review, longer if anything is questioned.
3. When it's approved, **assign your number(s)** to the campaign: on the campaign, or under Numbers → your number →
   Messaging. They're usually active within minutes.
4. Send a test from Settings → Send test text. A 40010 error means the number isn't attached to an approved campaign
   yet.

Telnyx fees (as published by Telnyx, July 2025; check the portal for current amounts):
- brand registration $4.50 one-time;
- campaign review $15 each time it goes to the carriers;
- campaign $1.50–$10 a month by use case (most $10; Low Volume Mixed $1.50; Sole Proprietor $2);
- carrier surcharges of roughly $0.003–$0.0045 per SMS on top of Telnyx's own rate.

Telnyx's 10DLC compliance guide (support.telnyx.com → "10DLC Campaign Compliance Guide") has the exact field
requirements.

#### VoIP.ms 10DLC

VoIP.ms registers brands and campaigns for you through its messaging team rather than a self-service form. Check
**voip.ms → Support / wiki: "10DLC Business Text Messaging Regulations and Requirements"** for the current request
form or address.
1. **Brand details.** Name, legal company name and DBA, website, industry, business address, a contact email and
   phone, and a business identifier: the wiki lists DUNS, GIIN or LEI, and US firms are normally registered by EIN, so
   have it ready.
2. **Campaign details.** The DID(s) to attach, the campaign type (use case), its attributes (opt-in/opt-out,
   links…), the sample messages, and the description and message flow above.
3. **Timing.** VoIP.ms says campaign vetting can take up to 15 days.
4. **Fees** (VoIP.ms wiki; may have changed): brand $4 one-time, campaign $15 one-time, then $1.50–$30 a month by use
   case (most $10). VoIP.ms has said it absorbs the carrier per-message surcharges for now.
5. **No automatic HELP reply.** VoIP.ms doesn't send the HELP and opt-out confirmation replies that Telnyx can send
   automatically. Carriers expect a reply to HELP and STOP. This app records STOP/START but doesn't currently send
   those confirmation texts, so arrange them before going live on VoIP.ms (see "Things to know with VoIP.ms" below).

#### Toll-free numbers instead

A toll-free number (8XX) doesn't use 10DLC. It needs **Toll-Free Verification** instead: a similar form with business
details, opt-in description and samples, usually free and approved in days to a few weeks. It's a reasonable
alternative if 10DLC is slow. On Telnyx it's under Messaging → Toll-Free Verification. Clients see an 800-type
number rather than a local one.

### Telnyx

1. **Buy or port a number** in the Telnyx Portal.
2. **Register it for business texting** (see [10DLC](#business-texting-registration-10dlc-start-this-first) above):
   Messaging → 10DLC for a local number, or Toll-Free Verification for a toll-free number. Texts won't be delivered
   until the number is attached to an approved campaign.
3. **Create a Messaging Profile.**
   - Assign the number to it.
   - Set **Webhook URL** to `https://<your public host>/api/webhooks/telnyx`.
   - Set **Webhook API version** to **v2**.
   - Leave Telnyx's STOP/HELP auto-responses on. If the profile lets you edit the HELP / opt-out text, make it match the
     replies in your campaign registration.
4. **Create an API key** under API Keys.
5. **Copy your public key** from Account Settings → Keys & Credentials → Public Key. It verifies webhook signatures.

### VoIP.ms

1. **Get an SMS-enabled DID.** In the VoIP.ms portal: DID Numbers → Manage DIDs → edit the number → **Message
   Service (SMS/MMS)** → enable it.
2. **Register for business texting** (see [10DLC](#business-texting-registration-10dlc-start-this-first) above).
   Ask VoIP.ms's messaging team to register your brand and campaign and attach the DID. Carriers block unregistered
   business traffic just as they do with Telnyx.
3. **Enable the API.** Main Menu → **SOAP and REST/JSON API**:
   - set an **API password** (it is separate from your login password);
   - **Enable API**;
   - under **Enable IP Addresses**, add the public IP address the server's outgoing traffic comes from. If the API
     check says "this server's public IP address isn't allowed", that's the setting to fix.
4. **In the app:** on Settings, choose **VoIP.ms**. Enter the account email, the API password and the DID, then
   **Save**. Saving creates the callback key and shows the full **callback URL** on the right. Click **Check the
   connection**, which reads your balance.
5. **Incoming texts.** Back on the DID's Message Service settings:
   - tick **Enable SMS/MMS URL Callback** and **URL Callback Retry**;
   - paste the callback URL from the Settings page, with your **public** webhook host name. It looks like
     `https://texthooks.yourfirm.com/api/webhooks/voipms?key=<key>&to={TO}&from={FROM}&message={MESSAGE}&id={ID}&date={TIMESTAMP}&media={MEDIA}`;
   - keep the URL private, because the key in it is what proves a callback came from VoIP.ms.
   The app answers `ok`, so VoIP.ms doesn't re-send. Opening the URL in a browser with only the `key=` part just
   says the URL works.
6. **Cloudflare Tunnel.**
   - Widen the tunnel route path to `^/api/webhooks/(telnyx|voipms)$`.
   - Use the VoIP.ms WAF expression the installer prints, which also allows `GET /api/webhooks/voipms`. VoIP.ms
     doesn't publish the IP addresses its callbacks come from, so that rule can't filter by IP.
   - To lock it down further, Settings shows **Last callback from** for the address of each good callback. Enter
     those addresses under **VoIP.ms → Advanced → Accept callbacks only from these IP ranges**, and in the WAF rule.

**Things to know with VoIP.ms**
- **Daily limit.** The API allows 100 texts a day unless VoIP.ms support raises it.
  - Set the same number in **Daily sending limit**.
  - When it's reached, texts wait in the queue and go out the next day (office time zone) rather than failing.
  - Bulk send warns you before a list would go over it.
  - When long texts are split into parts, each part counts toward the limit.
- **Bulk sending.** VoIP.ms says it may restrict accounts that look like bulk or marketing senders. Keep bulk sends
  to clients who agreed to texts, and talk to VoIP.ms if you plan large mailings.
- **No delivery receipts.** VoIP.ms doesn't report whether a text reached the phone. Texts show as "sent", and the
  delivery-rate figure in Reports only covers Telnyx texts.
- **STOP.** The app blocks further texts to anyone who replies STOP, whichever provider you use.
- **HELP and opt-out confirmations.** Your 10DLC campaign promises a reply to HELP and a confirmation after STOP.
  Telnyx can send these itself; VoIP.ms doesn't, and this app doesn't yet send them. Ask for that feature before going
  live on VoIP.ms.

## 2. Configuration

### Active Directory groups

Create two security groups in AD, or have IT do it:

| Group | Who | Can |
|---|---|---|
| `txtAppUser` | everyone who texts clients | use the inbox, clients, lists, bulk send and the message log; insert templates; set up auto-replies |
| `txtAppAdmin` | office manager / IT | everything above, plus Settings, Reports, editing templates and deleting clients |

Add yourself to `txtAppAdmin` before the first sign-in.
- Group changes reach the app the next time the person signs in to Windows.
- The app re-checks membership every 5 minutes.
- To use different group names, change them on the Settings page. Write them as `txtAppUser` or `DOMAIN\txtAppUser`.
  - The page won't let you move admin rights to a group you aren't in.
  - If you lock everyone out anyway, run `sql/reset-access-groups.sql`.

### Sign-in: Windows or local accounts

**Settings → Sign-in → How people sign in** chooses one of two methods. The change applies within seconds, with no
restart, and signs everyone out.

- **Windows** (the default). Staff are signed in automatically with their Windows account, and the two access groups
  are AD groups, as above. Needs the server on your domain.
- **Local.** Staff sign in with a user name and password managed on the app's **Users** page (admins only), plus a
  6-digit code from an authenticator app (Microsoft Authenticator, Google Authenticator, Authy, 1Password,
  Bitwarden…). Use it when the app is hosted outside your network or on a server that isn't on the domain. The two
  access groups are then local groups with the same names, which are created for you.

**Setting up Local sign-in**
1. While still on Windows sign-in, open **Users**, add yourself and tick `txtAppAdmin`. Note the temporary password
   it shows once.
2. Add everyone else (tick `txtAppUser`). Give each person their temporary password privately, in person or by phone.
3. On **Settings**, set **How people sign in** to **Local** and save. The page refuses if nobody is in the admin group
   yet.
4. At their first sign-in, each person sets up their authenticator app by scanning a QR code, then chooses their own
   password.

On a new server that isn't on the domain, the installer can do steps 1 and 3 itself:
`.\tools\Install-Server.ps1 -AppName RepairText -SqlServer .\SQLEXPRESS -SignIn Local -LocalAdminUser admin` (it asks for the password).

**How it's protected**
- Every sign-in needs the password **and** a current authenticator code. Each code works once.
- So do changes on **My account** (click your name in the top bar):
  - changing your name or email, or signing out other sessions, needs a code;
  - changing your password, or moving your authenticator to a new phone, needs your current password and a code.
- Admins signed in locally enter a code before changing users or groups. They aren't asked again for 5 minutes.
- Passwords are stored as salted PBKDF2 hashes (the ASP.NET Core Identity format). The authenticator secrets are
  encrypted with the same Data Protection keys as the Telnyx API key.
- Accounts lock after 5 wrong passwords or codes, for 15 minutes. Admins can unlock them early on the Users page.
  Sign-in attempts are also limited per IP address.
- Sessions end after 60 minutes idle, and after 12 hours in any case. Closing the browser also ends the session.
  These limits, the lockout settings and the minimum password length (12) are on the Settings page.
- Changing or resetting a password or authenticator, or disabling an account, ends that account's other sessions
  straight away.
- **Security log** (bottom of the Users page; exports to CSV) records every sign-in, failed attempt, lockout,
  password and authenticator change, and every user/group change, with who did it, when, and from which IP address.

**Lost phone or forgotten password.** On the Users page, open the person and choose **Reset authenticator** (they
set up a new one at next sign-in) or **Reset password** (a new temporary password, shown once).

**Locked out as the only admin.** Re-run the installer with `-LocalAdminUser <your name>`. It resets that account's
password, unlocks and re-enables it, and puts it back in the admin group; add `-ResetLocalAdminAuthenticator` if the
phone is lost too. Or run `sql/reset-sign-in-to-windows.sql` to go back to Windows sign-in.

**Hosting outside your network.** The webhook-only rule (Settings → "Cloudflare Tunnel traffic can only reach the
webhook") blocks staff pages through the tunnel. If staff will reach the app through Cloudflare, turn that setting off
and switch to Local sign-in *first*. Consider Cloudflare Access in front of the staff host as an extra layer.

### The config file holds only the database connection

`appsettings.json` contains nothing but the SQL connection:

```json
"ConnectionStrings": { "WebText": "Server=SQL01;Database=WebText;Trusted_Connection=True;TrustServerCertificate=True" },
"Database": { "Provider": "SqlServer", "CreateIfMissing": true }
```

(The `Logging` and `AllowedHosts` entries are standard ASP.NET Core defaults with no secrets.)

With `Trusted_Connection=True`, the app connects as the IIS app pool's Windows identity. That means there's no SQL
password in the file either.

### Everything else: the Settings page

Sign in as a `txtAppAdmin` member and open **Settings** (top bar).

| Section | Settings |
|---|---|
| Telnyx connection | API key (encrypted, never displayed again), sending number, messaging profile ID |
| Incoming texts | Telnyx public key, verify signatures (leave on), age limit, optional status URL override |
| Sending | messages per second, retry attempts, API base URL |
| Access | user group (default `txtAppUser`), admin group (default `txtAppAdmin`) |
| General | office name (top bar), time zone, cost per SMS segment (for the Reports estimate) |

The page also gives you these tools:
- **Check API key**, which reads your Telnyx balance and sends nothing.
- **Send test text** to your own phone.
- The webhook URL to paste into Telnyx, when the last webhook arrived, and any recent webhook errors.
- A history of every settings change and who made it.

Until the API key and sending number are saved, every page shows a "texting isn't set up yet" banner, and sending is
disabled.

**How the API key is protected.** It is encrypted with ASP.NET Core Data Protection before it's written to SQL.
- The encryption keys are also stored in SQL, in `DataProtectionKeys`.
- On Windows, those keys are themselves encrypted with the web server's machine key (DPAPI).
- So a stolen database backup alone can't reveal the API key.

The trade-off: if you move the app to a new server, or run two web servers, the new server can't decrypt the saved
key.
- The Settings page will say so; just re-enter the API key there.
- For a permanent web farm, protect the key ring with a shared certificate instead. See *Extending*.

### Creating the database

**The installer builds the database for you.** `tools\Install-Server.ps1` asks for the SQL Server and the database
name, or takes them as `-SqlServer` and `-Database`. It then runs the app's own setup mode,
`dotnet WebText.Web.dll --setup-database`, which does the following:
- creates the database if it doesn't exist
- creates every table from the app's data model, so the structure always matches the version you're installing
- upgrades a database made by an earlier version
- adds the starter templates
- gives the website's account read/write access, and nothing more

**Administrator rights.** Setting up the database needs a SQL Server administrator: sysadmin, or dbcreator plus
securityadmin.
- The installer first tries the Windows account you're running it as.
- If that isn't enough, it explains why and asks you to choose a **SQL Server login** (for example `sa`) or a
  **different Windows account**, then prompts for its username and password.
- To avoid the prompt, pass the credentials up front:
  `-SqlAdminCredential (Get-Credential sa)` or `-SqlAdminCredential (Get-Credential CONTOSO\sqladmin)`.
- The credentials are used only for the setup. They aren't saved, logged or written to any file. A SQL login's
  password reaches the setup step only through a temporary environment variable.

**Day to day,** the website connects with Windows authentication as its app pool account (`DOMAIN\SERVER$` for a
remote SQL Server), which has `db_datareader` and `db_datawriter` only. There's no password in `appsettings.json`.

**If your DBA prefers to do it by hand,** run the installer with `-SkipSqlSetup`. The DBA then:
- creates the database
- runs `sql/create-database.sql` in it
- grants the website's account `db_datareader` and `db_datawriter`

### Upgrading an existing database

The app creates tables only on an empty database. If you deployed an earlier version, run the upgrade scripts once,
in order, before deploying this one:
1. `sql/upgrade-001-recipient-lists.sql`
2. `sql/upgrade-002-settings-in-database.sql`
3. `sql/upgrade-003-auto-replies.sql`
4. `sql/upgrade-004-local-sign-in.sql`
5. `sql/upgrade-005-scheduled-backups.sql`

The installer's database step (`Install-Server.ps1`, or `dotnet WebText.Web.dll --setup-database`) runs all of these
for you, and they're safe to run more than once. For a local SQLite test database, delete `webtext-dev.db` to get the
new tables.

Then re-enter your Telnyx values on the Settings page, and delete them from the old `appsettings.json` or environment
variables.

## 3. Try it locally

**Quick setup (Windows 10/11).** In an elevated PowerShell window, signed in as your own account, run:
`Set-ExecutionPolicy -Scope Process Bypass; .\tools\Setup-DevWorkstation.ps1`
- It installs the .NET 10 SDK.
- It enables local IIS with the same features as the server, plus logging and Failed Request Tracing.
- It installs the ASP.NET Core 10 Hosting Bundle and unlocks the IIS authentication settings.
- It trusts the HTTPS development certificate.
- Add `-SkipIis` if you only debug with `dotnet run` / Visual Studio. Add `-IncludeSqlExpress` to test against
  SQL Server instead of SQLite.

To build the app you need the **.NET 10 SDK**. Visual Studio 2026 includes it. On its own, it works with VS Code or
the command line. Visual Studio 2022 can't build .NET 10 projects.

From the folder that contains `WebText.sln` (the top of the unzipped solution):

```powershell
cd .\src\WebText.Web
dotnet run --launch-profile https
```

Or, from anywhere, let PowerShell find the project:

```powershell
$proj = Get-ChildItem -Path "$HOME\Downloads" -Filter WebText.Web.csproj -Recurse | Select-Object -First 1
cd $proj.DirectoryName
dotnet run --launch-profile https
```

Change `$HOME\Downloads` to wherever you unzipped it.

In Development, the app uses a local SQLite file, so no SQL Server is needed. It also signs you in as `DEV\developer`,
who counts as a member of both groups, so no domain is needed.

To fake an inbound text, first set **Settings → Verify webhook signatures = No** on your dev copy only. Then run:

```powershell
curl -k -X POST https://localhost:7239/api/webhooks/telnyx -H "Content-Type: application/json" -d '{
  "data": { "id": "evt-test-1", "event_type": "message.received", "occurred_at": "2026-10-20T15:00:00Z",
    "payload": { "id": "msg-test-1", "text": "Did you get my W-2?",
      "from": { "phone_number": "+13125550100" }, "to": [ { "phone_number": "+13125550199" } ] } } }'
```

The text appears instantly in the inbox. To use a real Telnyx number locally, enter it on the Settings page. Expose
the webhook with a tunnel such as Cloudflare Tunnel.

On a domain-joined PC, remove `Auth:DevUser` from `appsettings.Development.json` to test with your real Windows login
and real group membership.

The Development file and its fake user are ignored when the app runs in Production.

## 4. Deploy to IIS

### The quick way: `tools\Install-Server.ps1`

Copy the unzipped solution to the server (Windows Server 2019 or newer, domain-joined). Then, in an elevated
PowerShell window, run the command below. If your Windows account isn't a SQL Server administrator, the script asks
for one: a SQL login such as `sa`, or another Windows account.

```powershell
Set-ExecutionPolicy -Scope Process Bypass
cd C:\path\to\WebText                      # the folder containing WebText.sln
.\tools\Install-Server.ps1 -AppName TaxText -SqlServer SQL01 -CloudflareTunnelToken <token> -PublicWebhookHost texthooks.yourfirm.com
```

Leave out `-AppName` and it asks: *"Application name"*. It does everything below, and when it finishes the site is running in IIS at `C:\websites\<appname>`:

0. **Name.** Asks for the installation's name (`-AppName`), unless given. The site, app pool, folder and database
   default to it (see [Installations](#installations-one-per-customer)).
1. **IIS.** Enables the IIS role and features: Windows Authentication, WebSockets, Application Initialization,
   logging/tracing and the management tools.
2. **Hosting Bundle.** Downloads the latest **ASP.NET Core 10 Hosting Bundle** from Microsoft, verifies its SHA-512
   checksum, installs it and restarts IIS. It's skipped if already current. Offline servers can use
   `-HostingBundlePath`.
3. **Build.** Builds the app from source; this needs the .NET 10 SDK, which the script installs, and access to
   nuget.org. Alternatively, pass `-PackagePath` pointing at a folder you published elsewhere with
   `dotnet publish src\WebText.Web -c Release -o publish`.
4. **Copy.** Copies the files to `C:\websites\<appname>` (e.g. `C:\websites\taxtext`). Change this with `-InstallPath`.
   - On an update, the running site is taken offline briefly.
   - Your `appsettings.json` is kept.
5. **Database connection.** Writes the connection into `appsettings.json`.
6. **App pool.** Creates it with No Managed Code, AlwaysRunning, no idle timeout and a nightly 3 AM recycle. It
   runs as the built-in identity, or as a service account with `-AppPoolCredential`.
7. **Site.** Creates the site with preload, Windows + Anonymous authentication (scoped to this site) and an HTTPS
   binding.
   - It uses a certificate matching `-HostName` (default: the server's DNS name) or `-CertificateThumbprint`.
   - If neither exists, it creates a self-signed one; replace that with one from your internal CA.
8. **Database.** Asks for the SQL Server and database name, then builds the database: it creates the database and
   all tables, or upgrades an existing one, adds the starter templates, and grants the website's account read/write.
   - It uses your Windows account if that's a SQL administrator. Otherwise it asks for a SQL login such as `sa`, or
     another Windows account. See *Creating the database*.
   - `-SkipSqlSetup` leaves SQL to your DBA.
9. **Firewall.** Opens TCP 443 for the **domain/private network only**. Nothing is opened to the internet.
10. **Cloudflare Tunnel.** With `-CloudflareTunnelToken`, installs Cloudflare's connector, `cloudflared`, as a Windows
    service.
    - It checks the installer's digital signature first.
    - Run the script again to update `cloudflared`, which doesn't update itself on Windows.
11. **Check.** Starts the site and requests it to confirm it responds.
12. **Summary.** Prints the exact Cloudflare and Telnyx settings to enter, and saves a log next to the install
    folder.

The script is safe to run again. Use the same command to deploy a new version or to repair the setup.

### Exposing the webhook with Cloudflare Tunnel (no inbound ports)

Staff use the app on the internal network only. The single thing the internet needs to reach is Telnyx's webhook,
`POST /api/webhooks/telnyx`, and Cloudflare Tunnel delivers it **without opening any port on your firewall**.
- `cloudflared` on the server makes an outbound connection to Cloudflare. It needs outbound TCP/UDP 7844 and
  TCP 443.
- Cloudflare passes Telnyx's requests down that connection.
- Tunnels are free with a free Cloudflare account. The catch: you need a domain whose DNS is hosted on Cloudflare
  (the Free plan is enough).
- Don't use the random `trycloudflare.com` quick tunnels. They're for testing and don't support the rules below.

```
Telnyx ──HTTPS──► Cloudflare edge ──(WAF: Telnyx IPs + webhook path only)──► tunnel ──► cloudflared on the server
                                                                                          └─► https://localhost (IIS)
```

**One-time setup in Cloudflare:**

1. **Create the tunnel.** In the Cloudflare dashboard, go to **Zero Trust → Networks → Tunnels** and create a
   **Cloudflared** tunnel named, for example, `webtext`.
   - Choose **Windows**, then copy the token: the long value after `cloudflared.exe service install`.
   - Pass it to the script as `-CloudflareTunnelToken`. You don't need to run Cloudflare's command yourself.
2. **Add a published application route** (called a "public hostname" in older dashboards) on the tunnel:
   - **Hostname:** e.g. `texthooks.yourfirm.com`
   - **Path:** `^/api/webhooks/telnyx$` (if your dashboard shows the field)
   - **Service:** `HTTPS` → `localhost:443`
   - **Additional application settings → TLS → No TLS Verify = On.** This hop never leaves the server. If the
     script reported a host-name binding, also set **Origin Server Name** and **HTTP Host Header** to that name.
3. **Add a WAF custom rule** under **Security → WAF → Custom rules** on that domain. Name it "Telnyx webhook only",
   set the action to **Block**, and paste the expression the script printed. It is also saved to
   `C:\websites\cloudflare-waf-rule.txt`. Its shape is:

   ```
   (http.host eq "texthooks.yourfirm.com" and (not ip.src in {<Telnyx webhook ranges>}
     or not (http.request.method eq "POST" and http.request.uri.path eq "/api/webhooks/telnyx")))
   ```

   The Free plan includes WAF custom rules, and this needs only one.
4. **Point Telnyx at it.** In the Telnyx Portal, set the messaging profile's webhook URL to
   `https://texthooks.yourfirm.com/api/webhooks/telnyx` (API v2).

**Three layers of protection on the public side:**

| Layer | What it does |
|---|---|
| Cloudflare WAF rule | Only Telnyx's published webhook IPs, and only `POST /api/webhooks/telnyx`, get through. Everything else is blocked at Cloudflare's edge and never reaches your network. |
| The app itself | Checks the caller's real IP (Cloudflare's `CF-Connecting-IP` header) against **Settings → Accept webhooks only from these IP ranges**, which is pre-filled with Telnyx's list. Any other tunnel request gets 404, even if the Cloudflare rules are ever misconfigured. Rejections are shown on the Settings page. |
| Telnyx signature | Every webhook must carry a valid Ed25519 signature from your Telnyx account, so even a request from a Telnyx IP can't be forged. |

**If Telnyx changes its IP addresses,** incoming texts stop arriving. You'll see "Rejected since restart" climbing
on the Settings page, and failed deliveries in Telnyx's webhook debugger. Update both places:
- the WAF rule
- the IP list on the Settings page

Telnyx's current list is on its support page "What IPs do I need to whitelist?", in the Webhooks section.

### Doing it by hand

1. **Prepare the server.** See *Server requirements* below. In short:
   - Windows Server 2016 or newer, **joined to the domain**.
   - IIS with Windows Authentication, WebSocket Protocol and Application Initialization. In an elevated PowerShell:

     ```powershell
     Install-WindowsFeature Web-Server, Web-Windows-Auth, Web-WebSockets, Web-AppInit, Web-Mgmt-Console
     ```

   - Then the **ASP.NET Core 10.0 Hosting Bundle** (the latest 10.0.x) from Microsoft's .NET download page.
     - Install it *after* IIS; if IIS was added later, run the installer again and choose Repair.
     - Then restart IIS: `net stop was /y` followed by `net start w3svc`.
2. **Publish:** `dotnet publish src/WebText.Web -c Release -o C:\websites\<appname>` (and add `"Instance": { "Name": "<AppName>" }` to its appsettings.json)
3. **Create the app pool.** .NET CLR version **No Managed Code**.
   - Under Advanced Settings, set **Start Mode = AlwaysRunning** and **Idle Time-out = 0**.
   - On the site, set **Preload Enabled = True**.

   These matter: the background sender only runs while the app is loaded. With IIS's default 20-minute idle
   shutdown, a queued bulk send would pause until someone next opens the site.
4. **Create the site.** Add an HTTPS binding with your certificate.
   - The included `web.config` turns on both Windows and Anonymous authentication.
   - Anonymous is needed only so Telnyx can reach the webhook. The app still requires a Windows login everywhere else.
   - If IIS says the section is locked, run the two `appcmd unlock` commands in `web.config`'s comment once.
5. **Grant SQL access** to the app pool identity. See the bottom of `sql/create-database.sql`.
6. **Expose the webhook** with Cloudflare Tunnel, as described above.
7. **Configure the app.** Browse to the site as a `txtAppAdmin` member, open **Settings**, and enter:
   - the Telnyx API key, sending number and public key
   - the time zone

   Click **Check API key**.
8. **Test the round trip.**
   - Use **Send test text** on the Settings page, or text a staff mobile from the inbox, and confirm it reaches
     "Delivered".
   - Reply from that phone and confirm the reply appears in the thread.
   - If replies don't show, check the **Incoming texts** panel on the Settings page, and Telnyx Portal → Debugging →
     Webhooks.

### Server requirements

| Item | Requirement |
|---|---|
| OS | Windows Server 2016 or newer (2022/2025 recommended), domain-joined |
| IIS features | Web Server, Windows Authentication, WebSocket Protocol, Application Initialization, IIS Management Console |
| Runtime | ASP.NET Core **10.0** Hosting Bundle, latest patch. Install Microsoft's monthly .NET updates (Windows Update / WSUS can deliver them). |
| Not needed | .NET SDK, the "ASP.NET 4.x" / ".NET Framework" IIS features, SQL client drivers (bundled with the app), URL Rewrite (only if IIS itself is your webhook reverse proxy) |
| Certificates | HTTPS cert for the staff URL (internal CA is fine). The public webhook name uses Cloudflare's certificate automatically. |
| Outbound | `api.telnyx.com` on 443 (allow through any web proxy); SQL Server on 1433; Cloudflare Tunnel: TCP/UDP 7844 and TCP 443 |
| Inbound | Staff PCs on 443 from the internal network only. **Nothing from the internet**: Telnyx reaches the webhook through Cloudflare Tunnel. |
| SQL login | The app pool identity. With the default identity and a remote SQL Server this is `DOMAIN\WEBSERVER$`. Grant `db_datareader` + `db_datawriter`, plus `db_ddladmin` for the first start. |
| Browsers | Add the site to the Local Intranet zone by group policy, so Edge and Chrome sign staff in silently |

**Support window.** .NET 10 is a long-term support release, supported until November 2028.

**Upgrading from the .NET 8 build.**
- Install the 10.0 Hosting Bundle. It can sit alongside 8.0.
- Publish this version over the old folder.
- Recycle the app pool.
- No database changes are needed for the retarget itself.

## 5. Day-to-day notes

- **Opt-outs.** STOP is honoured automatically, both by Telnyx at the carrier level and by the app. Staff can also
  tick "Opted out" on the client record.
- **Unknown numbers** show a "new #" tag. Open the client record to name it, or merge it into the right client.
- **Shared numbers.** When two client records share a number, a banner says so in the thread.
- **Attachments.** Picture messages (MMS) from clients are shown inline. Telnyx deletes the media after 30 days, so
  save anything important to the client's file.
- **SSNs are kept out of the logs.** Anything that looks like a Social Security number (or ITIN) is replaced with
  `***-**-****` before it's written to a log. That means `123-45-6789`, `123 45 6789`, `123.45.6789`, or any nine
  digits standing on their own. This covers:
  - the app's own log (IIS stdout, Windows Event Log), including error details and stack traces;
  - the stored copies of incoming webhooks;
  - send error details;
  - the security log;
  - the settings and auto-reply change histories.

  The VoIP.ms callback key is masked in the same way. The installer also stops IIS from recording query strings,
  because VoIP.ms puts the message text there. Re-running the installer masks entries saved before this was added.

  Phone numbers, ZIP+4 codes, dates and longer IDs are left alone. Account numbers of exactly nine digits will be
  masked too. **Message text in client threads is not changed**, so staff still see what the client actually sent.
- **What not to text.** Texts aren't encrypted end-to-end. Keep SSNs, account numbers, balances, passwords and other
  sensitive details out of them, and ask the client to call or use your portal instead. Follow your industry's rules
  and your written security plan (for tax preparers, IRS Publication 4557). The starter templates follow this rule.
- **Consent.** Text only clients who gave you their mobile number and agreed to receive texts. Your 10DLC campaign
  registration will ask how you collect consent.

### Auto-replies

Anyone in `txtAppUser` or `txtAppAdmin` can set these up on the **Auto-replies** page.

- **Keyword.** One word or a short phrase. Upper/lower case, extra spaces and punctuation are ignored, so `joke!`
  matches `JOKE`.
- **How it matches.**
  - *Whole text* (recommended): the client's text is just the keyword.
  - *Starts with*: `HOURS` matches "hours today?" but not "hourly".
  - *Anywhere*: the keyword appears as a word anywhere in the text. This can fire on ordinary conversation, so use it
    only for unusual words.
  - If several match, whole-text beats starts-with beats anywhere, and then the longest keyword wins.
- **Reply.** Up to 1600 characters, with `{FirstName}`, `{LastName}` and `{FullName}`. For numbers not yet linked to
  a client the names are left blank ("Hi {FirstName}, thanks" → "Hi, thanks").
- **Where it shows.** The reply appears in the client's thread and in the message log, sent by
  `Auto-reply (KEYWORD)`. By default the client's keyword text is marked as handled, so it doesn't add to the unread
  count; untick "Mark as handled" if staff should still see it as new.
- **Safeguards.**
  - STOP, START, HELP, INFO and the other opt-out words can't be keywords. The app and Telnyx already handle them.
  - Clients who have opted out never get auto-replies.
  - Each keyword has a cooldown (default 5 minutes) before the same client gets that same keyword's reply again.
    A different keyword still gets answered.
  - At most 20 auto-replies per client per hour (admins change this under Settings → Auto-replies), so the app can't get into a loop with
    another automated number.
  - Settings → Auto-replies also has a master switch that turns them all off at once.
- **Usage.** The list shows, for each keyword, how many times its reply has been sent, how many different phone
  numbers it went to, and when it was last sent. These are counted from the message history, so they match the
  threads and the message log. Renaming a keyword starts its counts again under the new name.
- **Change history.** Every add, change, turn on/off and delete is recorded with the person's Windows login and the
  time. A change lists only what changed (e.g. `Reply: "old" → "new"`, `Cooldown: "5 min" → "15 min"`). Adding or
  deleting records the full settings, so a deleted keyword can be recreated. The history stays after a keyword is
  deleted. It's at the bottom of the Auto-replies page, can be filtered by keyword, and exports to CSV. It is
  stored in the `AutoResponderChanges` table and can't be edited or cleared from the app.
- **Reports.** Auto-replies are counted separately on the "Texts sent" tile and in the CSV. They aren't credited to
  any staff member and don't count as a response in response-time figures.
- **10DLC.** Mention keyword auto-replies in your campaign description and sample messages. Carriers can filter
  texts that don't match what you registered.

### Backup and moving to a new server

**Settings → Backup & restore** (admins only).

- **What a backup contains:**
  - every setting, including the Telnyx/VoIP.ms API keys;
  - local users (with their password hashes and authenticator keys) and groups;
  - clients, recipient lists, templates and auto-replies;
  - optionally, the **text history** (every text and note, with picture links);
  - optionally, the **audit logs** (security log, settings and auto-reply change history).
- **It's encrypted with a password you choose** when you make it (at least 12 characters; AES-256-GCM).
  - Nobody can open it without that password, including you, so keep the password in your password manager.
  - The file still holds client details and credentials, so store it like client files.
- **Why the database alone isn't enough.** API keys and authenticator keys in the database are encrypted with keys that
  only work on the server that saved them. A backup decrypts them into the (password-encrypted) file and re-encrypts
  them on the new server.
- **Logged.** Making and restoring backups are recorded in the security log.

**Scheduled backups** (Settings → Scheduled backups)

These protect you if the server or its configuration is lost. The app writes a backup file on its own, on the
schedule you set.
- **Days and time.** For example every day at 23:00 office time, or only Mon/Wed/Fri. If the server was off or
  restarting at that time, the backup runs when it comes back (within 12 hours).
- **Folder.** A folder on the server, such as `C:\txtappbackup`, or a network share written as
  `\\fileserver\backups\txtapp`.
  - **Mapped drive letters (Z:) don't work.** The website runs as a service account that doesn't see them; the
    Settings page says so if you try.
  - Better still, use a share on another machine, or a folder your normal backup software copies off the server.
    A backup on the same disk won't survive that disk failing.
- **File names.** With **Add the date and time to each file name** on, each run makes a new file, such as
  `webtext-backup-2026-11-02_2300.wtbackup`, and older ones beyond **Keep this many backup files** (default 30) are
  deleted. Only files the app named are ever deleted. With it off, one `webtext-backup.wtbackup` is replaced each
  time.
- **What's included.** The text history and the audit logs are included by default; you can untick them.
- **Backup file password.** Every file is encrypted with this password.
  - **Record it somewhere other than this server** (e.g. your password manager). If the server is lost, the backup
    can't be opened without it.
  - The app stores it encrypted, so a copy of the database doesn't reveal it.
- **Folder permissions.** The website's account needs Modify permission on the folder.
  - Local folder: `Install-Server.ps1 -AppName TaxText -BackupFolder C:\txtappbackup` creates it with the right permissions. It also
    closes it to everyone except Administrators, SYSTEM and the website.
  - Network share: with the built-in app pool account, the website reaches the network as the server's **computer
    account** (`DOMAIN\SERVERNAME$`). Give that account Change on the share and Modify on the folder. The installer
    prints the exact name.
- **Back up to the folder now** on the Backup & restore page runs one straight away. Use it to test the folder and
  permissions. The page lists recent runs with the file written or the error.
- **If a scheduled backup fails,** or none has run when one was due, every admin sees a red warning at the top of every
  page until a backup succeeds. The error also goes to the Windows Event Log.
- **Several web servers?** Each scheduled slot runs on only one of them.
- **Restoring.** Restore a scheduled backup like any other: Backup & restore → Restore, with the backup password.

**Moving to a new server**
1. On the old server, make a backup (tick text history if you want it).
2. Install the app on the new server with `Install-Server.ps1`. For Local sign-in on a server outside the domain, use
   `-SignIn Local -LocalAdminUser setup` so you can sign in.
3. On the new server, open **Settings → Backup & restore**. Choose the file, enter its password, and click **Check the
   file** to see what's in it. Then type `REPLACE` and click **Restore and replace**.
4. **Restore replaces** the settings, users, groups, clients, lists, templates, auto-replies and text history on the
   new server. Its own encryption keys and security log are kept.
5. **Sign-in method.** "Keep this server's sign-in method" is ticked by default, so a server outside the domain
   doesn't switch to Windows sign-in. The restore refuses to leave a Local-sign-in server without a local admin.
6. **After restoring with Local sign-in,** everyone signs in with their accounts from the backup: same passwords, same
   authenticator apps. The temporary `setup` account is replaced.
7. **Update the webhook / callback URLs** at Telnyx or VoIP.ms if the public host name changed, and the Cloudflare
   tunnel if you moved it. Texts still queued in the backup aren't sent from the new server; they're marked
   "not sent" so staff can resend them.

Uploads of up to 500 MB are allowed. A backup with tens of thousands of texts is typically a few MB.

### Reading the reports

- **Delivery rate** is delivered ÷ (delivered + failed). Texts still in progress are excluded.
  - A low rate usually means bad numbers (landlines, typos), or a 10DLC registration problem.
  - Sort the client table by **Failed** to find the bad numbers.
- **Reply rate** is the share of clients texted in the period who texted back afterwards. It is a good sign of
  whether clients engage with texts.
- **Response time** runs from a client's text to the first staff text *or note* in that thread. A logged phone call
  counts as a response. Several texts in a row from a client count as one.
  - The clock runs around the clock, so a text received Friday evening and answered Monday counts as ~3 days.
  - **Awaiting a reply** lists client texts that nobody has followed up on yet.
- **SMS segments** are what carriers bill. A text over 160 characters (70 if it contains emoji) counts as several.

## 6. Extending

- **Web farm / moving servers.** To share the encrypted API key across servers, replace
  `ProtectKeysWithDpapi(protectToLocalMachine: true)` in `Program.cs` with
  `ProtectKeysWithCertificate(<cert from the machine store>)`. Install the same certificate on each server.

- **Schema changes.** The app uses `EnsureCreated`, which builds the schema once. Before changing the entities later,
  switch to EF Core migrations:

  ```powershell
  dotnet tool install -g dotnet-ef
  dotnet ef migrations add Initial -p src/WebText.Web
  ```

  Then set `Database:CreateIfMissing=false`, and add `db.Database.Migrate()` at startup or apply scripts from
  `dotnet ef migrations script --idempotent`.
- **Web farm.** The send worker is safe to run on several servers: rows are claimed atomically. The "who's viewing"
  presence and SignalR are per-server, though. For a farm, add a SignalR Redis backplane or Azure SignalR.
- **More numbers.** `FromNumber` is one setting. Supporting several departments' numbers means adding a
  `FromNumber` column per client or team.
