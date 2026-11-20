export const activity = [];
export async function call(path, method = "GET", body) {
  const started = performance.now();
  let status = 0,
    data,
    traceId;
  try {
    const response = await fetch(path, {
      method,
      headers:
        body instanceof FormData || body === undefined
          ? {}
          : { "Content-Type": "application/json" },
      body:
        body === undefined
          ? undefined
          : body instanceof FormData
            ? body
            : JSON.stringify(body),
    });
    status = response.status;
    traceId = response.headers.get("x-trace-id");
    const raw = await response.text();
    try {
      data = JSON.parse(raw);
    } catch {
      data = raw;
    }
    if (!response.ok) {
      const error = new Error(
        data?.detail || data?.error || data?.title || `HTTP ${status}`,
      );
      error.status = status;
      error.data = data;
      throw error;
    }
    return data;
  } finally {
    activity.unshift({
      method,
      path,
      status: status || "Network error",
      duration: Math.round(performance.now() - started),
      traceId,
      request: body instanceof FormData ? "[multipart PDF upload]" : body,
      response: data,
    });
    activity.length = Math.min(activity.length, 30);
    document.dispatchEvent(new Event("api-activity"));
  }
}
export const routes = [
  ["GET", "/", "Service readiness", false],
  ["POST", "/api/trips/validate", "Validate trip", false, "trip"],
  ["POST", "/api/trips/draft", "One-off draft (not saved)", true, "trip"],
  ["PUT", "/api/trip-runs/{id}", "Plan / resume persisted run", true, "trip"],
  ["GET", "/api/trip-runs/{id}", "Read saved run", false],
  ["GET", "/api/demo/budget", "Fictional budget example", false],
  [
    "POST",
    "/api/documents",
    "Upload PDF — use Knowledge upload form",
    false,
    "upload",
  ],
  ["POST", "/api/documents/{id}/index", "Index document", true, "index"],
  ["POST", "/api/chat", "RAG chat", true, "chat"],
  [
    "POST",
    "/api/travel-knowledge/search",
    "HTTP knowledge search",
    true,
    "search",
  ],
  ["GET", "/api/mcp/tools", "Discover MCP tools", false],
  ["POST", "/api/mcp/search", "MCP knowledge lookup", true, "search"],
  ["GET", "/api/local/capabilities", "RAG chat availability", false],
];
