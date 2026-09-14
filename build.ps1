# WideAgent 빌드
#
#   powershell -ExecutionPolicy Bypass -File build.ps1
#
# .NET Framework 4.x 의 csc.exe 만 있으면 된다. Windows 에 기본으로 들어 있어
# 따로 설치할 것이 없다.

param(
    [switch]$Icon,   # 아이콘을 앱 패키지 로고에서 다시 만든다
    [string]$Out = "$PSScriptRoot\WideAgent.exe"
)

$ErrorActionPreference = 'Stop'

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    throw "csc.exe 를 찾을 수 없습니다: $csc"
}

if ($Icon) {
    & powershell -ExecutionPolicy Bypass -File "$PSScriptRoot\src\make-icon.ps1"
}

$ico = "$PSScriptRoot\WideAgent.ico"

# AssemblyInfo.cs 는 exe 속성 창에 뜨는 제품명·회사명·버전을 넣는다.
$src = @("$PSScriptRoot\src\WideAgent.cs", "$PSScriptRoot\src\AssemblyInfo.cs")

# 트레이에 상주하므로 콘솔 창이 없는 winexe 로 만든다.
& $csc /nologo /target:winexe /out:$Out /win32icon:$ico `
    /reference:System.dll,System.Drawing.dll,System.Windows.Forms.dll `
    $src

if ($LASTEXITCODE -ne 0) { throw "빌드 실패 (exit $LASTEXITCODE)" }

Write-Host "빌드 완료: $Out"
