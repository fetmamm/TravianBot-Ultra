Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")

projectDir = fso.GetParentFolderName(WScript.ScriptFullName)
progressScriptPath = projectDir & "\scripts\Show-DevLaunchProgress.ps1"
If Not fso.FileExists(progressScriptPath) Then
    MsgBox "Launch progress script is missing: " & progressScriptPath, vbCritical, "Tbot Ultra"
    WScript.Quit 1
End If
progressStatusPath = shell.ExpandEnvironmentStrings("%TEMP%") & "\" & fso.GetTempName
progressReadyPath = progressStatusPath & ".ready"
progressCancelPath = progressStatusPath & ".cancel"
WriteLaunchStatus progressStatusPath, 4, "Checking launcher requirements..."
progressCommand = "powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & progressScriptPath & """ -StatusPath """ & progressStatusPath & """ -ReadyPath """ & progressReadyPath & """ -CancelPath """ & progressCancelPath & """"
shell.Run progressCommand, 0, False
For progressWait = 1 To 80
    If fso.FileExists(progressReadyPath) Then
        Exit For
    End If
    WScript.Sleep 50
Next
QuitIfLaunchCancelled
If Not fso.FileExists(progressReadyPath) Then
    WriteLaunchStatus progressStatusPath, -1, "Progress window could not be opened."
    MsgBox "The launch progress window could not be opened. The app was not started.", vbCritical, "Tbot Ultra"
    WScript.Quit 1
End If
dotnetExe = ResolveDotnetExe(shell, fso)
projectPath = projectDir & "\src\TbotUltra.Desktop\TbotUltra.Desktop.csproj"
devOutputDir = projectDir & "\temp_build_out\dev-app"
exePath = devOutputDir & "\TbotUltra.Desktop.exe"
sourceStampPath = devOutputDir & "\.source-stamp"
checkStampPath = devOutputDir & "\.ci-check-stamp"
checkLogPath = devOutputDir & "\preflight.log"
debugBuildLogPath = devOutputDir & "\dev-build.log"
checkScriptPath = projectDir & "\scripts\Verify-DevLaunch.ps1"
srcDesktop = projectDir & "\src\TbotUltra.Desktop"
srcWorker = projectDir & "\src\TbotUltra.Worker"
srcCore = projectDir & "\src\TbotUltra.Core"
srcDesktopTests = projectDir & "\src\TbotUltra.Desktop.Tests"
srcWorkerTests = projectDir & "\src\TbotUltra.Worker.Tests"
needsBuild = False

If Len(dotnetExe) = 0 Then
    WriteLaunchFailure progressStatusPath, ".NET SDK is missing.", "Install the .NET 8 SDK before starting Tbot Ultra.", ""
ElseIf Not fso.FileExists(projectPath) Then
    WriteLaunchFailure progressStatusPath, "Desktop project is missing.", projectPath, ""
Else
    QuitIfLaunchCancelled
    shell.CurrentDirectory = projectDir
    WriteLaunchStatus progressStatusPath, 7, "Checking changed source files..."

    latestSource = #1/1/2000#
    UpdateLatestModified srcDesktop, latestSource
    UpdateLatestModified srcWorker, latestSource
    UpdateLatestModified srcCore, latestSource
    latestCheckSource = latestSource
    UpdateLatestModified srcDesktopTests, latestCheckSource
    UpdateLatestModified srcWorkerTests, latestCheckSource
    If fso.GetFile(WScript.ScriptFullName).DateLastModified > latestCheckSource Then
        latestCheckSource = fso.GetFile(WScript.ScriptFullName).DateLastModified
    End If
    If fso.FileExists(checkScriptPath) Then
        If fso.GetFile(checkScriptPath).DateLastModified > latestCheckSource Then
            latestCheckSource = fso.GetFile(checkScriptPath).DateLastModified
        End If
    End If
    If fso.GetFile(progressScriptPath).DateLastModified > latestCheckSource Then
        latestCheckSource = fso.GetFile(progressScriptPath).DateLastModified
    End If

    If Not fso.FileExists(exePath) Then
        needsBuild = True
    ElseIf latestSource > ReadBuildStamp(fso, sourceStampPath) Then
        needsBuild = True
    End If

    If Not fso.FileExists(checkScriptPath) Then
        WriteLaunchFailure progressStatusPath, "Preflight script is missing.", checkScriptPath, ""
        WScript.Quit 1
    End If

    QuitIfLaunchCancelled
    If latestCheckSource > ReadBuildStamp(fso, checkStampPath) Then
        checkCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & checkScriptPath & """ -DotnetExe """ & dotnetExe & """ -LogPath """ & checkLogPath & """ -StatusPath """ & progressStatusPath & """ -CancelPath """ & progressCancelPath & """"
        checkExitCode = shell.Run(checkCommand, 0, True)
        QuitIfLaunchCancelled
        If checkExitCode <> 0 Then
            WriteLaunchFailure progressStatusPath, "Build or tests failed. The app was not started.", _
                ReadPreflightFailure(fso, checkLogPath), checkLogPath
            WScript.Quit checkExitCode
        End If
        WriteBuildStamp fso, checkStampPath, latestCheckSource
    Else
        QuitIfLaunchCancelled
        WriteLaunchStatus progressStatusPath, 82, "Build and tests are already up to date."
    End If

    QuitIfLaunchCancelled
    If IsAppRunning(shell) Then
        If Not shell.AppActivate("Tbot Ultra") Then
            WriteLaunchFailure progressStatusPath, "Tbot Ultra is already running.", _
                "The existing app could not be activated. Close it before rebuilding the dev app output.", ""
            WScript.Quit 1
        Else
            WriteLaunchStatus progressStatusPath, 100, "Tbot Ultra is already running."
        End If
        WScript.Quit 0
    End If

    If needsBuild Then
        QuitIfLaunchCancelled
        WriteLaunchStatus progressStatusPath, 86, "Building the desktop app..."
        buildCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & checkScriptPath & """ -DotnetExe """ & dotnetExe & """ -LogPath """ & debugBuildLogPath & """ -StatusPath """ & progressStatusPath & """ -CancelPath """ & progressCancelPath & """ -DebugBuildOnly -OutputPath """ & devOutputDir & """"
        buildExitCode = shell.Run(buildCommand, 0, True)
        QuitIfLaunchCancelled
        If buildExitCode <> 0 Then
            WriteLaunchFailure progressStatusPath, "Desktop build failed. The app was not started.", _
                ReadPreflightFailure(fso, debugBuildLogPath), debugBuildLogPath
        ElseIf Not fso.FileExists(exePath) Then
            WriteLaunchFailure progressStatusPath, "Built app was not found.", exePath, debugBuildLogPath
        Else
            WriteBuildStamp fso, sourceStampPath, latestSource
            WriteLaunchStatus progressStatusPath, 96, "Opening Tbot Ultra..."
            QuitIfLaunchCancelled
            LaunchAndActivate shell, exePath
            WriteLaunchStatus progressStatusPath, 100, "Tbot Ultra is ready."
        End If
    Else
        WriteLaunchStatus progressStatusPath, 96, "Opening Tbot Ultra..."
        QuitIfLaunchCancelled
        LaunchAndActivate shell, exePath
        WriteLaunchStatus progressStatusPath, 100, "Tbot Ultra is ready."
    End If
End If

Sub QuitIfLaunchCancelled
    If fso.FileExists(progressCancelPath) Then
        On Error Resume Next
        fso.DeleteFile progressCancelPath, True
        WScript.Quit 2
    End If
End Sub

Sub WriteLaunchStatus(statusPath, progressValue, message)
    On Error Resume Next
    Set statusStream = fso.OpenTextFile(statusPath, 2, True)
    statusStream.Write CStr(progressValue) & "|" & message
    statusStream.Close
End Sub

Sub WriteLaunchFailure(statusPath, message, detail, logPath)
    On Error Resume Next
    Set statusStream = fso.OpenTextFile(statusPath, 2, True)
    statusStream.WriteLine "-1|" & message
    statusStream.WriteLine detail
    If Len(logPath) > 0 Then
        statusStream.WriteLine logPath
    Else
        statusStream.WriteLine "-"
    End If
    statusStream.Close
End Sub

Function IsAppRunning(shellObject)
    On Error Resume Next
    Dim exitCode
    exitCode = shellObject.Run("cmd /c tasklist /FI ""IMAGENAME eq TbotUltra.Desktop.exe"" | find /I ""TbotUltra.Desktop.exe"" >nul 2>nul", 0, True)
    IsAppRunning = (exitCode = 0)
End Function

Function ReadPreflightFailure(fileSystemObject, logPath)
    ReadPreflightFailure = "Check the preflight log for the failed step."
    If Not fileSystemObject.FileExists(logPath) Then
        Exit Function
    End If

    Set stream = fileSystemObject.OpenTextFile(logPath, 1)
    Do Until stream.AtEndOfStream
        line = Trim(stream.ReadLine)
        If InStr(1, line, "[FAIL]", vbTextCompare) > 0 Or _
           InStr(1, line, "ERROR:", vbTextCompare) = 1 Or _
           InStr(1, line, "error CS", vbTextCompare) > 0 Then
            ReadPreflightFailure = Left(line, 300)
            Exit Do
        End If
    Loop
    stream.Close
End Function

Sub UpdateLatestModified(folderPath, ByRef latest)
    On Error Resume Next
    If Not fso.FolderExists(folderPath) Then
        Exit Sub
    End If

    Set folder = fso.GetFolder(folderPath)
    folderName = LCase(folder.Name)
    If folderName = "bin" Or folderName = "obj" Then
        Exit Sub
    End If

    ' Directory timestamps do not reliably change when an existing file is edited.
    For Each sourceFile In folder.Files
        If sourceFile.DateLastModified > latest Then
            latest = sourceFile.DateLastModified
        End If
    Next

    For Each subFolder In folder.SubFolders
        UpdateLatestModified subFolder.Path, latest
    Next
End Sub

Function ReadBuildStamp(fileSystemObject, stampPath)
    On Error Resume Next
    ReadBuildStamp = #1/1/2000#
    If Not fileSystemObject.FileExists(stampPath) Then
        Exit Function
    End If

    Set stream = fileSystemObject.OpenTextFile(stampPath, 1)
    value = Trim(stream.ReadLine)
    stream.Close
    If IsDate(value) Then
        ReadBuildStamp = CDate(value)
    End If
End Function

Sub WriteBuildStamp(fileSystemObject, stampPath, sourceModified)
    On Error Resume Next
    Set stream = fileSystemObject.OpenTextFile(stampPath, 2, True)
    stream.Write CStr(sourceModified)
    stream.Close
End Sub

Function ResolveDotnetExe(shellObject, fileSystemObject)
    On Error Resume Next

    Dim preferredPath
    preferredPath = "C:\Program Files\dotnet\dotnet.exe"
    If fileSystemObject.FileExists(preferredPath) Then
        ResolveDotnetExe = preferredPath
        Exit Function
    End If

    Dim whereOutputPath
    whereOutputPath = shellObject.ExpandEnvironmentStrings("%TEMP%") & "\tbot_dotnet_path.txt"
    shellObject.Run "cmd /c where dotnet > """ & whereOutputPath & """ 2>nul", 0, True
    If fileSystemObject.FileExists(whereOutputPath) Then
        Dim stream, resolvedPath
        Set stream = fileSystemObject.OpenTextFile(whereOutputPath, 1)
        resolvedPath = Trim(stream.ReadAll)
        stream.Close
        On Error Resume Next
        fileSystemObject.DeleteFile whereOutputPath, True

        If Len(resolvedPath) > 0 Then
            ResolveDotnetExe = Split(resolvedPath, vbCrLf)(0)
            Exit Function
        End If
    End If

    ResolveDotnetExe = ""
End Function

Sub LaunchAndActivate(shellObject, executablePath)
    On Error Resume Next

    shellObject.Run """" & executablePath & """ --dev-launcher", 1, False

    Dim attempts
    For attempts = 1 To 20
        WScript.Sleep 250
        If shellObject.AppActivate("Tbot Ultra") Then
            Exit For
        End If
    Next
End Sub
