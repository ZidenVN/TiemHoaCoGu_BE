# ==============================================================================
# Script khởi động tự động Tiệm Hoa Có Gu (Backend + Frontend)
# ==============================================================================

Write-Host "Dang khoi dong He thong Tiem Hoa Co Gu..." -ForegroundColor Green

$bePath = $PSScriptRoot
$fePath = Resolve-Path "$PSScriptRoot/../TiemHoaCoGu"

# 1. Mo Terminal khoi dong Backend .NET 8 (Port 5012)
Write-Host "-> Mo Backend ASP.NET Core (http://localhost:5012)..." -ForegroundColor Cyan
Start-Process powershell -ArgumentList "-NoExit", "-Command", "Write-Host '--- BACKEND .NET 8 (Port 5012) ---' -ForegroundColor Green; cd '$bePath'; dotnet run --project HoaSap.API/HoaSap.API.csproj --launch-profile 'http'"

# 2. Mo Terminal khoi dong Frontend Next.js (Port 3000)
Write-Host "-> Mo Frontend Next.js (http://localhost:3000)..." -ForegroundColor Cyan
Start-Process powershell -ArgumentList "-NoExit", "-Command", "Write-Host '--- FRONTEND NEXT.JS (Port 3000) ---' -ForegroundColor Yellow; cd '$fePath'; npm run dev"

Write-Host "`nDa khoi dong xong ca 2 service trong 2 cua so rieng biet!" -ForegroundColor Green
Write-Host "  * Frontend Storefront: http://localhost:3000" -ForegroundColor White
Write-Host "  * Frontend Admin CMS : http://localhost:3000/quan-tri" -ForegroundColor White
Write-Host "  * Backend Swagger API: http://localhost:5012/swagger" -ForegroundColor White
