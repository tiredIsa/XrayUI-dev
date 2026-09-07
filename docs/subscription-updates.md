# Automatic subscription updates

New subscriptions default to refreshing every 6 hours; choose Off, 1, 6, 12 or
24 hours when adding or editing a subscription. Existing subscriptions keep their
saved setting. The interval is measured from the last successful refresh.

While the app is running, overdue subscriptions refresh directly if its proxy is
off, or through its local SOCKS proxy if it is on. Failed proxy requests never
fall back to direct access. The scheduler checks on startup, every minute, when
the network becomes available, and when the proxy connects. Closing the app
pauses scheduling; overdue updates resume on the next launch.

Failed refreshes keep the previous servers. Transient errors and invalid/empty
responses retry after 1, 5, 15, 30, then 60 minutes (hourly thereafter). HTTP
401/403/404 wait the normal interval and show a link/access error. HTTP 429 honors
Retry-After, including for manual refreshes; without that header it uses the
retry delay. A restored network can retry transient failures immediately, and
connecting the proxy can retry a failed direct request, subject to rate limits.
When no network interface is available, no request or attempt is recorded.

