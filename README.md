# Souls-PRODUCT

SoulsDLC account API and web account dashboard. The launcher runs Minecraft 1.21.4 / Fabric with AutoBuy 2.4.2 Protected.

## Railway

Deploy from the repository root with `api/Dockerfile`. Configure `DATABASE_URL`, `ADMIN_KEY`, and `LAUNCHER_PROOF_SECRET` as Railway service variables. Never commit their values.

The PostgreSQL connection is provided by a Railway Postgres service. `/health` checks both the API and its database connection.

Launcher binaries and protected mod artifacts are excluded from Git. The website redirects downloads to the launcher release asset on GitHub. Override `LAUNCHER_DOWNLOAD_URL` to publish a newer build without changing the API.

Optional SMTP/Resend settings enable password recovery emails; configure these separately in Railway variables.
