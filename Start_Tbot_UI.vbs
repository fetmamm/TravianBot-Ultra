Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")

projectDir = fso.GetParentFolderName(WScript.ScriptFullName)
dotnetExe = "C:\Program Files\dotnet\dotnet.exe"
projectPath = projectDir & "\src\TbotUltra.Desktop\TbotUltra.Desktop.csproj"
exePath = projectDir & "\src\TbotUltra.Desktop\bin\Debug\net8.0-windows\TbotUltra.Desktop.exe"

If Not fso.FileExists(dotnetExe) Then
    MsgBox ".NET SDK is missing. Install .NET 8 SDK first.", vbExclamation, "Tbot Ultra"
ElseIf Not fso.FileExists(projectPath) Then
    MsgBox "Desktop project file is missing: " & projectPath, vbExclamation, "Tbot Ultra"
Else
    shell.CurrentDirectory = projectDir
    buildExitCode = shell.Run("""" & dotnetExe & """ build """ & projectPath & """ -c Debug -nologo", 0, True)
    If buildExitCode <> 0 Then
        MsgBox "Build failed. Exit code: " & buildExitCode, vbExclamation, "Tbot Ultra"
    ElseIf Not fso.FileExists(exePath) Then
        MsgBox "Built exe not found: " & exePath, vbExclamation, "Tbot Ultra"
    Else
        shell.Run """" & exePath & """", 0, False
    End If
End If
