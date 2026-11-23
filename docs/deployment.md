# Advanced deployment and networking

For Windows application installation, Docker Desktop setup, updates and source-checkout Docker commands, see the [root installation instructions](../README.md#installation).

- [Reverse proxy TLS termination](#reverse-proxy-tls-termination)
- [HTTP and optional HTTPS](#http-and-optional-https)

## Reverse proxy TLS termination

A reverse proxy can terminate public HTTPS and forward requests to Lineup over private HTTP. Lineup ignores forwarded request headers by default. Add the immediate proxy IP address or CIDR network under **Settings → General → Network Settings → Known Proxies** only when the shared management listener is behind that proxy. The persisted list applies to the Management UI, APIs, and management-hosted virtual outputs. An empty list disables forwarded request metadata.

Changing Known Proxies gracefully restarts Lineup so the management request pipeline can apply the new trust boundary. Docker images and prebuilt packages use the same persisted setting in `/appdata/settings.json` or `%LOCALAPPDATA%\Lineup\data\settings.json`; no environment-specific proxy configuration is required.

The proxy must preserve the original `Host` header and provide `X-Forwarded-For` and `X-Forwarded-Proto`. Lineup processes one trusted proxy hop and ignores `X-Forwarded-Host`. For Nginx:

```nginx
location / {
    proxy_pass http://lineup:8080;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
}
```

Caddy, Traefik, and other proxies normally preserve or forward the equivalent metadata automatically, but their immediate connection address must still match a configured Known Proxies entry. Do not trust an unrestricted network or expose the internal Lineup listener directly when forwarded headers are enabled. Trusted metadata lets Lineup enforce Lineup Link HTTPS requirements, advertise correct public HDHomeRun URLs, attribute streams to the original client, and preserve loopback-only worker enrollment.

Dedicated-listener virtual outputs are separate HTTP servers and do not inherit the management listener's Known Proxies. Configure **Honor Forwarded Headers** and **Trusted Proxies** in that virtual output's advanced listener security settings only when the dedicated listener is itself behind a reverse proxy. For proxy TLS termination, select HTTP as the internal listener transport, override the client-facing base URL with the public HTTPS root URL, and trust the immediate proxy address observed by the worker. Lineup Link then requires HTTPS from clients while accepting the proxy's private HTTP connection only when its trusted `X-Forwarded-Proto` value is `https`; direct unproxied HTTP remains rejected. Saving a dedicated policy recycles only the affected isolated worker; the main Lineup host and other outputs remain running. Direct HTTP or HTTPS listeners should leave forwarded headers disabled.

## HTTP and optional HTTPS

Lineup always listens for HTTP on `Lineup:HttpPort` (8080 by default). HTTPS is optional and is enabled only when `Lineup:Https:CertificatePath` points to a readable, currently valid PFX containing a private key. Certificate provisioning remains outside the unauthenticated Settings UI so private keys and passwords are not persisted to `settings.json` or exposed through the browser.

For Docker Compose, name the certificate `lineup.pfx`, place it in the local `./certs` directory, and provide its password at startup:

```bash
CERT_PASSWORD=****** docker compose -f docker-compose.prod.yml up -d
```

Use deployment-platform secrets rather than source-controlled environment files for the password. The production Compose file mounts `./certs` at `/app/certs:ro` and configures Lineup to load `/app/certs/lineup.pfx`. It publishes container port `8443` on the host-facing `HTTPS_PORT` (8443 by default). Similarly, `HTTP_PORT` selects the host-facing port mapped to fixed container port `8080`. `HDHOMERUN_DISCOVERY_PORT` and `SSDP_PORT` select the host-facing UDP ports mapped to fixed container ports `65001/udp` and `1900/udp`; changing these defaults can prevent clients from discovering Lineup automatically. The normal `docker-compose.yml` remains HTTP-only.

Outside the production Compose deployment, HTTPS remains optional and is enabled by setting `Lineup:Https:CertificatePath`. If the configured certificate is missing, inaccessible, invalid, missing its private key, expired, or not yet valid, Lineup logs a warning without exposing the password and continues with HTTP only. The corresponding environment variables are `Lineup__Https__CertificatePath` and `Lineup__Https__CertificatePassword`. Certificate and endpoint changes require a Lineup restart. HTTP remains intentionally available when HTTPS is active; Lineup does not force HTTPS redirection.

## Related documentation

[Project overview](../README.md) | [Contributor standards](../CONTRIBUTING.md)
