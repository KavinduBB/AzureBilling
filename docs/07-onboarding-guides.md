# 07 — Onboarding Guides (customer-facing copy)

These are the three remediation guides shown from the onboarding checklist. Keep them plain, one screen each. Screenshots to be added by design.

---

## Guide A — Connect your organisation (Graph consent)

**Who can do this:** a Global Administrator or Privileged Role Administrator in your Microsoft Entra tenant.

**What we ask for and why**

| Permission | What it lets us read | What it does not do |
|---|---|---|
| Read organisation information | Your purchased licences and subscription renewal dates | Nothing about individual users |
| Read all users' basic profiles | Which licences are assigned to which people, and whether accounts are active | Cannot read email, files, messages, or passwords; cannot change anything |

We never buy, assign, or remove licences with these permissions. You can revoke access at any time from **Entra admin centre → Enterprise applications → MLCP → Permissions**.

**Steps**
1. Click **Connect as administrator**.
2. Sign in with your admin account (MFA may be required).
3. Review the permissions and click **Accept**.
4. You'll return here; discovery takes 1–2 minutes.

**If you are not an administrator:** click **Send request to my admin** — we'll email them this page with a one-click link.

---

## Guide B — Unlock Azure costs (Azure role assignment)

**Who can do this:** someone with **Owner** or **User Access Administrator** on your Azure subscriptions or management group.

**Why consent alone isn't enough:** Azure uses its own permission system. Accepting the app permissions in Guide A gives us no access to Azure cost data. You need to grant our application the **Cost Management Reader** role — read-only — on the scope you want visible.

**Fastest path (all subscriptions):**
1. Open the **Deploy to Azure** button below (it deploys `customer-rbac.bicep`).
2. Choose the **Tenant Root Group** as the scope.
3. Confirm. One assignment covers every current and future subscription.

**Manual path (specific subscriptions):**
1. Azure portal → **Subscriptions** → select one → **Access control (IAM)**.
2. **Add → Add role assignment → Cost Management Reader**.
3. Assign access to **User, group, or service principal** → search **MLCP** → Select → Review + assign.
4. Repeat per subscription.

Cost data may take up to an hour to appear after assignment (Microsoft propagation delay). If you have an Enterprise Agreement, also ask your Enterprise Administrator to enable **"AO view charges"** and **"DA view charges"** in the enrolment settings.

---

## Guide C — Unlock prices and invoices (billing role)

**Applies to:** organisations on a **Microsoft Customer Agreement** (most direct customers; if you bought online before 2023 you may still be on the older agreement — it moves automatically at your next renewal).

**Who can do this:** a **Billing account owner**. A Global Administrator can make themselves a billing account owner from the Azure portal (*Cost Management + Billing → Access control (IAM) → Elevate access*).

**Why a third step:** billing permissions are separate from both app consent (Guide A) and Azure resource permissions (Guide B). We need read-only access to your billing account.

**Steps**
1. Azure portal → **Cost Management + Billing** → select your billing account.
2. **Access control (IAM) → Add**.
3. Role: **Billing account reader** (for prices, subscriptions, renewals) and **Invoice manager** if you want invoice downloads.
4. Member: search **MLCP** → Add.

What unlocks: per-seat prices you actually pay, monthly invoices, auto-renew status, term end dates, and (if enabled by your MLCP Owner) the ability to change quantities or auto-renew from the dashboard.

---

## Guide D — Usage insights and the privacy setting

**Applies to:** tenants that have granted **Read all usage reports** (an optional, separate consent).

By default Microsoft hides user names in usage reports. We can show **how many** licensed users are active per service without changing anything. To see **which** users or departments are inactive, your admin must turn off *"Display concealed user, group, and site names in all reports"* in **Microsoft 365 admin centre → Settings → Org settings → Reports**. This changes what every admin in your organisation sees in Microsoft's own reports too. MLCP will never change this setting for you; it records when you tell us you've changed it.

---

## Guide E — If your licences come from a partner

If your Microsoft licences are sold to you by a Cloud Solution Provider, Microsoft doesn't give you (or us) access to what your partner charges. You have two options:

1. **Enter your prices** — under *Prices*, type the per-seat price from your partner's invoice. All cost views will use it and show an "entered by you" badge.
2. **Invite your partner** — send them the invite from *Settings → Partner*. If they join MLCP and choose to share pricing with you, prices and renewal settings appear automatically.

Everything else — licences, who has what, usage, renewal dates — works without either step.
