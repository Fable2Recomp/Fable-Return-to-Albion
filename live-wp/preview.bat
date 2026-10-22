@echo off
echo Starting local preview server on http://localhost:8080 ...
start http://localhost:8080
python -m http.server 8080 --directory "%~dp0"
pause
