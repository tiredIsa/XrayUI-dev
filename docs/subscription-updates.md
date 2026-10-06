# Automatic subscription updates

New subscriptions default to refreshing every 6 hours; choose Off, 1, 6, 12 or
24 hours when adding or editing a subscription. Existing subscriptions keep their
saved setting. The interval is measured from the last successful refresh.

Subscriptions refresh only while the proxy is disconnected and no connection
start, stop or restart is in progress. This applies to scheduled and manual
refreshes, including adding a subscription and changing its URL. Requests use
direct access. The scheduler checks on startup, every minute, and when the
network becomes available. Overdue updates wait until the connection is stopped;
closing the app pauses scheduling until the next launch. If a connection starts
during a fetch, its result does not replace servers or record a successful refresh.
Subscription refreshes never switch the connection to another server.

Failed refreshes keep the previous servers. Transient errors and invalid/empty
responses retry after 1, 5, 15, 30, then 60 minutes (hourly thereafter). HTTP
401/403/404 wait the normal interval and show a link/access error. HTTP 429 honors
Retry-After, including for manual refreshes; without that header it uses the
retry delay. A restored network can retry transient failures immediately.
Retries remain subject to rate limits and the disconnected-only refresh policy.
When no network interface is available, no request or attempt is recorded.

