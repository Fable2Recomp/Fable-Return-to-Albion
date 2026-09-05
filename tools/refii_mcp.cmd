@echo off
rem Windows launcher for the refii-live MCP server: .mcp.json runs bare python3, which many
rem Windows installs do not have. Point .mcp.json's command at this file there.
where python3 >nul 2>nul && (python3 "%~dp0refii_mcp\__main__.py" & exit /b)
where python  >nul 2>nul && (python  "%~dp0refii_mcp\__main__.py" & exit /b)
py -3 "%~dp0refii_mcp\__main__.py"
