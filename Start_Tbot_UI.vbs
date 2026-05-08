Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")

projectDir = fso.GetParentFolderName(WScript.ScriptFullName)
dotnetExe = "C:\Program Files\dotnet\dotnet.exe"
projectPath = projectDir & "\src\TbotUltra.Desktop\TbotUltra.Desktop.csproj"
exePath = projectDir & "\src\TbotUltra.Desktop\bin\Debug\net8.0-windows\TbotUltra.Desktop.exe"
srcDesktop = projectDir & "\src\TbotUltra.Desktop"
srcWorker = projectDir & "\src\TbotUltra.Worker"
srcCore = projectDir & "\src\TbotUltra.Core"
needsBuild = False

If Not fso.FileExists(dotnetExe) Then
    MsgBox ".NET SDK is missing. Install .NET 8 SDK first.", vbExclamation, "Tbot Ultra"
ElseIf Not fso.FileExists(projectPath) Then
    MsgBox "Desktop project file is missing: " & projectPath, vbExclamation, "Tbot Ultra"
Else
    shell.CurrentDirectory = projectDir
    shell.Run "cmd /c taskkill /IM TbotUltra.Desktop.exe /F >nul 2>nul", 0, True

    If Not fso.FileExists(exePath) Then
        needsBuild = True
    Else
        exeModified = fso.GetFile(exePath).DateLastModified
        latestSource = #1/1/2000#
        UpdateLatestModified srcDesktop, latestSource
        UpdateLatestModified srcWorker, latestSource
        UpdateLatestModified srcCore, latestSource

        If latestSource > exeModified Then
            needsBuild = True
        End If
    End If

    If needsBuild Then
        buildExitCode = shell.Run("""" & dotnetExe & """ build """ & projectPath & """ -c Debug -nologo -m:1 -p:NuGetAudit=false", 0, True)
        If buildExitCode <> 0 Then
            MsgBox "Build failed. Exit code: " & buildExitCode & ". The app was not started, so you do not accidentally run an old build.", vbExclamation, "Tbot Ultra"
        ElseIf Not fso.FileExists(exePath) Then
            MsgBox "Built exe not found: " & exePath, vbExclamation, "Tbot Ultra"
        Else
            shell.Run """" & exePath & """", 1, False
        End If
    Else
        shell.Run """" & exePath & """", 1, False
    End If
End If

Sub UpdateLatestModified(folderPath, ByRef latest)
    On Error Resume Next
    If Not fso.FolderExists(folderPath) Then
        Exit Sub
    End If

    Set folder = fso.GetFolder(folderPath)
    For Each file In folder.Files
        ext = LCase(fso.GetExtensionName(file.Name))
        If ext = "cs" Or ext = "xaml" Or ext = "csproj" Or ext = "json" Or ext = "resx" Then
            If file.DateLastModified > latest Then
                latest = file.DateLastModified
            End If
        End If
    Next

    For Each subFolder In folder.SubFolders
        UpdateLatestModified subFolder.Path, latest
    Next
End Sub
