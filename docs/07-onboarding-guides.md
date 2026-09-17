# 07 — Onboarding Guides (customer-facing copy)

These are the remediation guides linked from the onboarding checklist. Each should stay plain and
fit on one screen. Design will add screenshots.

MLCP appears in the customer's tenant as **two enterprise applications** (ADR-015):
- **MLCP**: sign-in, licences, and the application that Azure and billing roles are granted to.
- **MLCP Usage Insights**: usage reports only.

---

## Guide A — Connect your organisation (Microsoft Graph consent)

**Who can do this:** a **Global Administrator** or **Privileged Role Administrator** in your
Microsoft Entra tenant. Other administrator roles can't approve these permissions.

**What we ask for and why**

| Permission (as shown by Microsoft) | What it lets us read | What it does not do |
|---|---|---|
| Read organization information | Your organisation's name and verified domains, your purchased licences, and your subscription renewal dates | Nothing about individual users |
| Read all users' full profiles | Which licences are assigned to which people, whether accounts are enabled, and profile fields such as department | It can't read email, files, messages, calendars or passwords, and it can't change anything |

We never buy, assign or remove licences with these permissions. You can revoke access at any time
in **Microsoft Entra admin center → Enterprise apps → MLCP → Permissions**.

**Steps**
1. Check the region shown ("Your organisation's data will be stored in the EU"), then click
   **Connect as administrator**.
2. Sign in with your admin account. Microsoft may ask for MFA.
3. Review the permissions and click **Accept**.
4. You'll come back here. We confirm the connection with Microsoft, which usually takes a few
   seconds. If Microsoft is still setting things up, we retry automatically for up to 15 minutes.
5. Discovery then takes a minute or two.

**If you are not an administrator:** click **Send request to my admin**. We email this page, with
a one-click link, to an address in your organisation's verified domains.

---

## Guide B — Unlock Azure costs (Azure role assignment)

**Who can do this:** someone with **Owner**, **User Access Administrator** or **Role Based Access
Control Administrator** on the management group or subscriptions you want to share.

**Why consent alone isn't enough:** Azure has its own permission system. Accepting the permissions
in Guide A gives us no access to Azure cost data. You also need to give the **MLCP** application
the read-only **Cost Management Reader** role.

**First, find MLCP's Object ID in your tenant.** You need it in both paths below.
1. Open **Microsoft Entra admin center → Enterprise apps → All applications**.
2. Select **MLCP**, not *MLCP Usage Insights*.
3. Open **Properties** and copy the **Object ID**. Do **not** use the Application (client) ID.

**Fastest path (management group):**
1. Click **Deploy to Azure**. This opens our role-assignment template in the Azure portal.
2. Choose the management group. **Tenant Root Group** covers every current and future
   subscription under it.
3. Paste the Object ID into **MLCP Principal Id**, then **Review + create**.

**Manual path (specific subscriptions):**
1. Azure portal → **Subscriptions** → select one → **Access control (IAM)**.
2. **Add → Add role assignment → Cost Management Reader**.
3. Assign access to **User, group, or service principal**, search for **MLCP**, then **Select →
   Review + assign**.
4. Repeat for each subscription.

**Good to know**
- **Microsoft Customer Agreement or partner (CSP) customers:** Microsoft doesn't support
  management groups for your subscriptions in Cost Management. MLCP reads each subscription
  separately, which is slower for large estates. Purchases such as reservations and Marketplace
  appear only with billing access. **Also complete Guide C** to see all costs.
- **Enterprise Agreement customers:** ask your Enterprise Administrator to turn on **Account
  owners can view charges** (and **Department admins can view charges** if you use departments).
  This is under **Cost Management + Billing → Billing scopes → your billing account → Settings →
  Policies**.
- **Microsoft Customer Agreement customers:** your billing profile's **Azure charges** policy must
  be on.
- Cost data can take up to an hour to appear after the role is assigned.

---

## Guide C — Unlock prices and invoices (billing role)

**Applies to:** organisations with a **Microsoft Customer Agreement** (MCA). If we couldn't tell
your agreement type yet, try this guide: it's also how we find out.

If you bought Microsoft 365 online some time ago, your account may still be on the older
Microsoft Online Subscription Agreement, and prices aren't available through Microsoft's APIs for
that agreement. Microsoft is moving direct customers to the Customer Agreement, and prices appear
here once your account has moved. Until then, you can enter seat prices yourself.

**Who can do this:** a **Billing account owner** or **Billing profile owner**.
- A Global Administrator can make themselves a billing account owner. Go to **Cost Management +
  Billing → Billing scopes**, select the option to view all billing accounts, pick your account,
  then **Access control (IAM) → Add → Billing account owner**.
- This works for Microsoft Customer Agreements only.

**Why a third step:** billing permissions are separate from both app consent (Guide A) and Azure
roles (Guide B).

**Steps**
1. Azure portal → **Cost Management + Billing → Billing scopes** → select your **billing
   account**.
2. **Access control (IAM) → Add**. Role: **Billing account reader**. This unlocks prices,
   subscriptions, renewals and billing-scope costs.
3. Search for **MLCP** (the application), select it, and **Add**.
4. **Optional, for invoice downloads:** open the **billing profile** (Cost Management + Billing →
   Billing profiles → your profile), then **Access control (IAM) → Add → Invoice manager → MLCP**.
   Invoice manager can only be assigned on a billing profile.

**What unlocks:**
- the per-seat prices you actually pay;
- monthly invoices;
- auto-renew status and term end dates;
- Azure purchases such as reservations and Marketplace;
- if your MLCP Owner turns it on later (Guide F), changing quantities or auto-renew from MLCP.

**Enterprise Agreement customers:** the portal can't assign EA billing roles to an application.
Your Enterprise Administrator (enrollment writer) assigns the **EnrollmentReader** role to MLCP's
Object ID through Microsoft's REST API instead
([how to](https://learn.microsoft.com/en-us/azure/cost-management-billing/manage/assign-roles-azure-service-principals)).
Until then, you can upload your EA price sheet.

---

## Guide D — Usage insights (optional second connection) and the privacy setting

**What this is:** usage reports come from a **separate application, MLCP Usage Insights**, with
exactly one permission: **Read all usage reports**. It's separate so you can grant it, or later
revoke it, without affecting anything else in MLCP.

**Who can do this:** a **Global Administrator** or **Privileged Role Administrator**.

**Steps**
1. On the checklist, click **Connect usage insights**.
2. Sign in as an administrator and **Accept** the single permission.
3. You'll come back here. Usage appears after the next daily sync. Microsoft's reports themselves
   lag by one to two days.

To revoke it, go to **Microsoft Entra admin center → Enterprise apps → MLCP Usage Insights** and
remove the permission or delete the application. Your main MLCP connection keeps working.

**The privacy setting.** By default, Microsoft hides user names in usage reports.
- Without changing anything, we can show **how many** licensed users are active in each service.
- To see **which** users or departments are inactive, a Global Administrator must **uncheck**
  *"Conceal user, group, and site names in all reports"*. It's in **Microsoft 365 admin center →
  Settings → Org settings → Services → Reports**.
- This setting also changes what every admin in your organisation sees in Microsoft's own reports.
- MLCP never changes this setting for you. We record when you tell us you've changed it, and we
  check the current value with Microsoft.

---

## Guide E — If your licences come from a partner

If a Cloud Solution Provider sells you your Microsoft licences, Microsoft doesn't give you (or us)
access to what your partner charges. You have two options:

1. **Enter your prices.** Under *Prices*, type the per-seat price from your partner's invoice.
   Every cost view uses it and shows an "entered by you" badge.
2. **Invite your partner.** Send them an invitation from *Settings → Partner*. If they join MLCP
   and choose to share pricing with you, prices and renewal settings appear automatically.

Everything else works without either step: licences, who has what, usage, and renewal dates.

For Azure costs through a partner, you need an **Azure plan**; follow Guide B with the
per-subscription path.

---

## Guide F — Allow changes from MLCP (Subscription Manager) — *Phase 5*

> Stub. The copy is finalised with P5-1.

**What this is:** MLCP can change subscription quantities, turn auto-renew on or off, and cancel
within Microsoft's allowed window. It does this only for people your organisation has chosen, and
always in their own name (ADR-010, ADR-023).

**All four of these are required:**
1. **Your Entra administrator assigns the role.** Go to **Microsoft Entra admin center →
   Enterprise apps → MLCP → Users and groups → Add user/group → role *Subscription Manager***.
   Nobody at MLCP can grant it, and your Entra audit log records it.
2. **An MLCP Owner turns on *Lifecycle operations*** in *Settings*.
3. **The person has a Microsoft billing role** that allows the change, such as billing profile
   contributor. Microsoft enforces this on every call.
4. **Each change is confirmed** on a screen that shows its financial impact, and is recorded in
   the audit log before it's sent to Microsoft.

Being an MLCP Owner or a Global Administrator doesn't include this permission.
