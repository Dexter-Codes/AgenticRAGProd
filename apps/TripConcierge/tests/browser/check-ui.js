async (page) => {
  const checks = [];
  const check = (value, name) => {
    if (!value) throw new Error(name);
    checks.push(name);
  };
  const errors = [];
  page.on("pageerror", (e) => errors.push(e.message));
  let saved = null,
    puts = 0,
    gets = 0,
    conflict = false,
    disabled = false,
    chatCalls = 0;
  const id = "11111111-1111-4111-8111-111111111111";
  const source = {
    chunkId: "guide-1",
    documentId: id,
    documentName: "Hanoi guide",
    pageNumber: 3,
    passage:
      "Temple of Literature is a visitor attraction. <img src=x onerror=alert(1)>",
    context: { sectionPath: ["Attractions"], warnings: ["Extraction warning"] },
    isTruncated: false,
    hasUnprocessedImages: true,
  };
  const destination = {
    id: 1581130,
    name: "Hanoi",
    country: "Vietnam",
    admin1: "Hanoi",
    timezone: "Asia/Bangkok",
  };
  const money = (amount) => ({ amount, currency: "INR" });
  await page.route("**/api/**", async (route) => {
    const req = route.request(),
      url = new URL(req.url()),
      path = url.pathname;
    const json = (body, status = 200) =>
      route.fulfill({
        status,
        contentType: "application/json",
        headers: { "X-Trace-Id": "mock-trace-123" },
        body: JSON.stringify(body),
      });
    if (path === "/api/local/capabilities")
      return json({ chatEnabled: !disabled });
    if (path === "/api/trips/validate") {
      const r = req.postDataJSON();
      if (r.origin.trim().toLowerCase() === r.destination.trim().toLowerCase())
        return json(
          {
            title: "Validation failed",
            errors: { Destination: ["Origin and destination must differ."] },
          },
          400,
        );
      return json({ status: "validated", trip: r });
    }
    if (path.startsWith("/api/trip-runs/")) {
      if (req.method() === "GET") {
        gets++;
        return saved
          ? json(saved)
          : json({ error: "Trip run not found." }, 404);
      }
      puts++;
      if (conflict)
        return json(
          {
            error:
              "The run is active or has just advanced. Retry the same request.",
          },
          409,
        );
      const r = req.postDataJSON();
      const original = { ...r, selectedDestinationId: null };
      const runId = path.split("/").pop();
      const lookup = {
        status: r.selectedDestinationId
          ? "destination_confirmed"
          : "selection_required",
        candidates: [
          destination,
          {
            ...destination,
            id: 2,
            name: "Hanoi alternative",
            admin1: "Other region",
          },
        ],
        attribution: "GeoNames via Open-Meteo",
        selectedDestination: r.selectedDestinationId ? destination : null,
      };
      let draft = {
        runId,
        status: "awaiting_destination_selection",
        destinationLookup: lookup,
        weather: null,
      };
      let checkpoint = {
        lastCompletedStage: "destination_lookup",
        destination: lookup,
      };
      if (r.selectedDestinationId) {
        const weather = {
          status: "outside_forecast_window",
          days: [],
          attribution: "Open-Meteo",
          checkedAt: "2026-11-19",
          availableFrom: "2026-11-19",
          availableThrough: "2026-12-04",
        };
        const budget = {
          budget: money(60000),
          total: money(40000),
          remaining: money(20000),
          isWithinBudget: true,
          lines: [
            {
              description: "Fictional return transport",
              quantity: 2,
              unitPrice: money(15000),
              total: money(30000),
              priceSource: "demo-fixture-v1",
            },
          ],
        };
        draft = {
          ...draft,
          status: "within_budget_demo",
          budget,
          weather,
          budgetRevision: {
            status: "revised_within_budget",
            initialTotal: money(65000),
            changes: [
              { previousOptionId: "expensive", replacementOptionId: "cheap" },
            ],
          },
          knowledge: { status: "retrieved", passages: [source] },
          itinerary: {
            status: "outline_ready",
            notice: "Generic, unpriced activity templates.",
            activityCatalog: { rest: "Rest or unscheduled time." },
            days: [
              {
                date: r.departureDate,
                morningActivityId: "rest",
                afternoonActivityId: "rest",
                eveningActivityId: "rest",
              },
            ],
          },
        };
        checkpoint = {
          ...checkpoint,
          lastCompletedStage: "itinerary_planning",
          weather,
          budgetDraft: draft,
          knowledge: draft.knowledge,
          itinerary: draft.itinerary,
        };
      }
      saved = {
        runId,
        request: original,
        selectedDestinationId: r.selectedDestinationId ?? null,
        draft,
        checkpoint,
        version: 2,
      };
      await new Promise((resolve) => setTimeout(resolve, 1800));
      return json(draft);
    }
    if (path === "/api/documents")
      return json({
        documentId: id,
        fileName: "guide.pdf",
        status: "Uploaded",
        sizeBytes: 9,
      });
    if (path.endsWith("/index"))
      return json({ documentId: id, status: "Indexed", chunks: 2 });
    if (path === "/api/chat") {
      chatCalls++;
      return json({
        conversationId: id,
        messageId: "message-1",
        answer: "Visit the temple [1].",
        answerKind: "answered",
        sources: [{ ...source, number: 1 }],
      });
    }
    if (path === "/api/travel-knowledge/search")
      return json({ status: "retrieved", passages: [source] });
    if (path === "/api/mcp/search")
      return json({
        structuredContent: { status: "retrieved", passages: [source] },
      });
    if (path === "/api/mcp/tools")
      return json([
        {
          name: "search_travel_knowledge",
          description: "Retrieve passages with citations",
          schema: {
            type: "object",
            properties: {
              destinationId: { type: "integer" },
              query: { type: "string" },
            },
          },
        },
      ]);
    if (path === "/api/demo/budget") return json({ total: money(60000) });
    return json(
      { error: "Blocked unexpected request in mocked UI check" },
      501,
    );
  });
  await page.goto("http://localhost:5090/ui/");
  await page.evaluate(() => localStorage.clear());
  await page.reload();
  await page.setViewportSize({ width: 1440, height: 1050 });
  await page.screenshot({
    path: "output/playwright/trip-desktop.png",
    fullPage: true,
  });
  check(
    (await page.title()) === "TripConcierge · Your next chapter",
    "UI served by .NET",
  );
  check(
    (await page.locator("[name=departureDate]").inputValue()) !== "",
    "Relative dates initialized",
  );
  await page.locator("[name=destination]").fill("Bengaluru");
  await page.getByRole("button", { name: "Validate inputs" }).click();
  await page.getByRole("alert").waitFor();
  check(
    (await page.locator("#plan-message").innerText()).includes(
      "Origin and destination must differ",
    ),
    "Server validation displayed",
  );
  await page.locator("[name=destination]").fill("Hanoi");
  await page
    .getByRole("button", { name: "Start planning", exact: true })
    .click();
  await page
    .getByRole("button", { name: "Select & continue" })
    .first()
    .waitFor();
  check(puts === 1, "Single initial PUT");
  check(gets > 0, "Planning polls read endpoint");
  const before = puts;
  await page.reload();
  await page
    .getByRole("button", { name: "Select & continue" })
    .first()
    .waitFor();
  check(puts === before, "Refresh restores selection without planning");
  await page.getByRole("button", { name: "Select & continue" }).first().click();
  await page.getByText("Your daily outline", { exact: true }).waitFor();
  check(puts === 2, "Destination resumes same run");
  check(
    saved.selectedDestinationId === 1581130,
    "Selected destination persisted",
  );
  await page.getByRole("button", { name: "[1] Hanoi guide · p. 3" }).click();
  await page.locator("dialog[open]").waitFor();
  check(
    (await page.locator("#source-content").innerText()).includes(
      "<img src=x onerror=alert(1)>",
    ),
    "Untrusted source shown as text",
  );
  check(
    (await page.locator("#source-content img").count()) === 0,
    "Source markup never parsed",
  );
  await page.getByRole("button", { name: "Close", exact: true }).click();
  await page.screenshot({
    path: "output/playwright/trip-result.png",
    fullPage: true,
  });
  await page.reload();
  await page.getByText("Your daily outline", { exact: true }).waitFor();
  check(puts === 2, "Completed result restored without PUT");
  await page.locator("[name=destination]").fill("Da Nang");
  await page.getByRole("button", { name: "Replay / resume same run" }).click();
  await page.getByRole("alert").waitFor();
  check(puts === 2, "Editing original prevents same-run mutation");
  conflict = true;
  await page
    .getByRole("button", { name: "Start planning", exact: true })
    .click();
  await page.getByRole("alert").waitFor();
  check(
    (await page.locator("#plan-message").innerText()).includes("active"),
    "409 shown with explicit retry",
  );
  check(puts === 3, "No automatic retry after conflict");
  conflict = false;
  await page.getByRole("link", { name: "Knowledge & chat" }).click();
  await page
    .locator("[name=file]")
    .setInputFiles({
      name: "guide.pdf",
      mimeType: "application/pdf",
      buffer: Buffer.from("%PDF-1.4"),
    });
  await page.getByRole("button", { name: "Upload PDF", exact: true }).click();
  await page.locator("#upload-result pre").waitFor();
  check(
    (await page.locator("#index-form [name=documentId]").inputValue()) === id,
    "Upload populates indexing ID",
  );
  await page
    .getByRole("button", { name: "Index document · embeddings" })
    .click();
  await page.locator("#index-result pre").waitFor();
  await page.locator("#chat-form [name=query]").fill("What can I visit?");
  await page
    .getByRole("button", { name: "Send question · models + embeddings" })
    .click();
  await page.getByText("Visit the temple [1].", { exact: true }).waitFor();
  check(
    (await page.locator("#conversation-id").inputValue()) === id,
    "Chat reuses conversation ID",
  );
  check(chatCalls === 1, "Chat submitted once");
  await page.getByRole("link", { name: "Retrieval & MCP" }).click();
  await page.locator("#retrieval-form [name=destinationId]").fill("1581130");
  await page.locator("#retrieval-form [name=query]").fill("Attractions");
  await page
    .getByRole("button", { name: "Retrieve passages · embeddings" })
    .click();
  await page.locator("#retrieval-result .source-list button").waitFor();
  await page.locator("[name=transport]").selectOption("mcp");
  await page
    .getByRole("button", { name: "Retrieve passages · embeddings" })
    .click();
  await page.locator("#retrieval-result .source-list button").waitFor();
  await page.getByRole("button", { name: "Discover tools" }).click();
  await page.locator("#tools-result summary").waitFor();
  check(
    (await page.locator("#tools-result").innerText()).includes(
      "search_travel_knowledge",
    ),
    "MCP discovery tool schema available",
  );
  await page.getByRole("link", { name: "API explorer" }).click();
  await page.locator("#operation").selectOption("5");
  await page.getByRole("button", { name: "Execute once" }).click();
  await page.locator("#explorer-result summary").click();
  await page.locator("#explorer-result pre").waitFor();
  check(
    (await page.locator("#activity").innerText()).includes("ms"),
    "Request status and timing shown",
  );
  await page.setViewportSize({ width: 390, height: 844 });
  await page.getByRole("link", { name: "Plan a trip" }).click();
  await page.screenshot({
    path: "output/playwright/trip-mobile.png",
    fullPage: true,
  });
  check(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth,
    ),
    "Mobile has no horizontal page overflow",
  );
  await page.getByRole("link", { name: "Saved runs" }).click();
  await page.locator("#open-run [name=runId]").fill(id);
  await page.getByRole("button", { name: "Open run", exact: true }).click();
  await page.getByText("Your daily outline", { exact: true }).waitFor();
  check(puts === 3, "Opening a run only reads");
  disabled = true;
  await page.reload();
  await page.getByRole("link", { name: "Knowledge & chat" }).click();
  await page.getByText("Local chat is disabled.", { exact: false }).waitFor();
  check(
    await page.locator("#chat-send").isDisabled(),
    "Disabled chat has clear availability state",
  );
  check(
    errors.length === 0,
    `No JavaScript runtime errors: ${errors.join(", ")}`,
  );
  return { passed: checks.length, checks, puts, gets, chatCalls };
}
