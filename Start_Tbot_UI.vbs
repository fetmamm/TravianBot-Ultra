Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")

projectDir = fso.GetParentFolderName(WScript.ScriptFullName)
dotnetExe = "C:\Program Files\dotnet\dotnet.exe"
projectPath = projectDir & "\src\TbotUltra.Desktop\TbotUltra.Desktop.csproj"

If Not fso.FileExists(dotnetExe) Then
    MsgBox ".NET SDK is missing. Install .NET 8 SDK first.", vbExclamation, "Tbot Ultra"
ElseIf Not fso.FileExists(projectPath) Then
    MsgBox "Desktop project file is missing: " & projectPath, vbExclamation, "Tbot Ultra"
Else
    shell.CurrentDirectory = projectDir
    shell.Run """" & dotnetExe & """ run --project """ & projectPath & """ -c Debug", 0, False
End If
