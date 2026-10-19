# Security policy

## Reporting a vulnerability

Please **do not open a public issue** for security problems. Report them privately through GitHub:
**[Report a vulnerability](https://github.com/OWNER/RambiBox/security/advisories/new)** (Security tab → *Report a vulnerability*).

Include what you found, how to reproduce it, the RambiBox version (Settings → About) and what an attacker could achieve.
You will get an answer within a few days. Once a fix is released, the advisory is published and you are credited
(unless you prefer not to be).

Only the latest release is supported with security fixes.

## What is exposed at all

RambiBox is a desktop app. It makes **no internet connections** and has no telemetry or auto-update. The only
network-facing part is the **phone color calibration server**. It runs only while a calibration is open and talks
only to devices on your local network.

Everything else stays on the PC:

- the serial port to the LED controller;
- the settings in `%AppData%\RambiBox`;
- the logs in `%LocalAppData%\RambiBox\logs`.

## The phone calibration server

When you start a calibration, RambiBox starts a small built-in HTTP(S) server and shows a QR code. It has these
protections (`src/RambiBox.Core/Calibration`):

| Protection | Details |
|---|---|
| **Secret token** | A new 128-bit random token (`RandomNumberGenerator`) is made for each calibration. It is only in the URL path from the QR code (`/c/<token>`). Tokens are compared in constant time (`CryptographicOperations.FixedTimeEquals`). A wrong token or an unknown route gets a plain 404. The only route without a token is `/ca`, which serves the public CA certificate. |
| **Private addresses only** | Connections are accepted only from loopback, `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`, `169.254.0.0/16` and IPv6 link-local. IPv4-mapped IPv6 addresses are checked as IPv4. Any other address, public or not listed, is closed without a response. |
| **Bound to the first phone** | The first device that opens the calibration page owns the session. From then on, every other address gets *403 – session in use*. API calls are refused before the page has been opened. This check runs before a request body is read. |
| **Connection caps** | At most 16 connections in total and 4 per address; extra connections get 503. One request per connection. |
| **Request limits** | Headers: 16 KiB. Body: 40 MiB; chunked transfer encoding is refused. One camera frame: 8 MiB, decoded at 1920 px at most. Video batches: up to 16 frames. Fine-tune JSON: 4 KiB. Only one analysis runs at a time; extra requests get 429. |
| **Time limits** | TLS handshake + headers: 5 s. Request body: 120 s. Response: 30 s. The server stops by itself after **45 minutes**, and when you close the calibration. |
| **Ports & firewall** | TCP 47800–47820. The optional firewall rule (Calibration page → *Allow…*, needs admin) applies to the RambiBox executable and to **private** networks only. |
| **Response headers** | `Cache-Control: no-store`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `X-Frame-Options: DENY`, a restrictive `Content-Security-Policy` and `Permissions-Policy: camera=(self)`. |
| **No data kept** | Camera frames (or video frames) are decoded and analyzed in memory. Only derived color measurements are kept, in RAM, until the calibration ends. Nothing is written to disk or sent anywhere. |

### The local certificate authority (live camera over HTTPS)

Browsers allow live camera access only on secure (HTTPS) pages. So RambiBox creates its **own local root
certificate** on the PC (`LocalCertificateAuthority.cs`). You install it on the phone once.

- **Name constraints.** The CA certificate has *critical* X.509 name constraints. It can only vouch for:
  - the private IPv4 ranges `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16` and `169.254.0.0/16`;
  - names under `.local`.

  Even if its key were stolen, it could not be used to impersonate any public website.
- **Path length 0.** The CA cannot issue intermediate CAs (`pathLen=0`). Its key usage is limited to signing
  certificates and CRLs.
- **Keys.** RSA 2048 with SHA-256.
  - The CA is valid for 10 years. It is renewed when less than about 14 months are left.
  - The server certificate is valid for 396 days and has a random serial. It covers the PC's LAN IP and
    `<pc-name>.local`, and is renewed when the IP or name changes.
- **Private keys never leave the PC.** Both private keys are stored in `%AppData%\RambiBox\phone-ca`, encrypted
  with Windows **DPAPI** (current-user scope). Key buffers are zeroed after use.
  - The phone only gets the public CA certificate, as a removable iOS profile or a `.cer` file.
  - Nothing is installed into the Windows certificate store.
- **Revocable at any time.**
  - On the PC: *Calibration → Reset phone certificate* deletes the CA and its keys. A new one is created the next
    time it is needed.
  - On the phone: remove the profile or certificate (iPhone: Settings → General → VPN & Device Management).

**Without that setup, no certificate is needed at all.** The phone then records a short video instead, which is
uploaded over plain HTTP on your local network. The same token, address and session rules apply.

### Threat model in short

The calibration server assumes that your home network is **not hostile**, but it does not trust it blindly:

- **Devices on the same network** can't use the server without the token from the QR code on your screen.
- **Once your phone is connected**, no other device can take over the session.
- **The internet** can't reach the server, because public addresses are refused even if a port is forwarded.

Someone who can read your screen, or can intercept plain HTTP on your Wi-Fi during the calibration, could see the
token. The worst they could do is disturb that one calibration; it would not let them into your PC.

## Other notes

- **The serial port.** Opening a serial port can reset Arduino-like boards. Automatic detection therefore only
  probes:
  - ports that can plausibly be an LED controller, with back-off;
  - an unknown port at most once when it appears.

  Bluetooth and modem ports are never opened.
- **Releases.** Release builds are made by GitHub Actions from the tagged source (`.github/workflows/release.yml`).
  Each release lists the SHA-256 checksum of `RambiBox.exe`. The exe is not code-signed, so Windows SmartScreen may
  warn on first start.
