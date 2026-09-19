// exe 속성 창과 SmartScreen 경고에 표시되는 정보다.
//
// csc 는 이 특성들로 Win32 버전 리소스를 만들어 준다. 비워 두면 속성 창이
// 전부 빈칸인 실행 파일이 되는데, 백신 휴리스틱에도 좋을 것이 없고 받는
// 사람이 무엇인지 확인할 방법도 없다.
//
// 릴리스할 때 버전 두 줄을 태그와 맞춰 올린다.

using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("WideAgent")]
[assembly: AssemblyProduct("WideAgent")]
[assembly: AssemblyDescription("Claude와 ChatGPT 데스크톱의 대화창을 넓게 유지한다")]
[assembly: AssemblyCompany("miniwhalelabs")]
[assembly: AssemblyCopyright("Copyright (c) 2026 miniwhalelabs")]

[assembly: AssemblyVersion("1.0.4.0")]
[assembly: AssemblyFileVersion("1.0.4.0")]

[assembly: ComVisible(false)]
