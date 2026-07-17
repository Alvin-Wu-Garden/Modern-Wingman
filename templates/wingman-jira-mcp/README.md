# Wingman Jira MCP Plugin template

This template wraps [`edrich13/mcp-jira-server`](https://github.com/edrich13/mcp-jira-server) as a Wingman-only Plugin. It is intentionally incomplete until packaged: the publish artifact must include the upstream `build/` directory and its production `node_modules/` directory under `vendor/mcp-jira-server/`.

Create a runnable artifact with:

```powershell
.\tools\package-jira-plugin.ps1 -SourceRoot C:\src\mcp-jira-server -OutputPath C:\artifacts\wingman-jira-mcp
```

Then choose the generated output directory in **Marketplace → Sources → Import folder**. In **Wingman Plugins**, install it, open **設定**, enter `JIRA_BASE_URL` and `JIRA_PAT`, and enable it. Wingman resolves the Node runtime from its managed/bundled runtime and never runs `npm`, `npx`, or a Plugin lifecycle script on the user's machine.

`JIRA_USER_AGENT` is intentionally not required; add it as a normal environment value to `.mcp.json` only when the Jira reverse proxy requires one.
