module PdfGenerator

    open System
    open System.IO

    [<RequireQualifiedAccess>]
    type LogEntry = 
        | Info of string 
        | Error of string

    module internal File = 

        let getDirectories (path:string) = 
            if Path.HasExtension(path)
            then Path.GetDirectoryName(path)
            else path 
            |> fun path -> path.Split([|"/"|], StringSplitOptions.RemoveEmptyEntries)

        let getRelativePath (baseLocation:string) (targetLocation:string) = 
            let targetLocation = Path.GetDirectoryName(targetLocation)
            targetLocation.Replace(baseLocation, "")

        let copyFile targetName fileName =
            try
                let fi = FileInfo(fileName)
                let target = 
                    let dir = Directory.CreateDirectory(targetName)
                    Path.Combine(dir.FullName, fi.Name)
                if not(File.Exists(target))
                then fi.CopyTo(target)
                else FileInfo(target)
            with e -> 
                raise(Exception(sprintf "Unable to copy file %A to %A" fileName targetName, e))

        let copyFileWithSubfolder baseDir target fileName =
            let fileName = Path.GetFullPath fileName
            let baseDir = Path.GetFullPath baseDir
            //let relative = (getRelativePath baseDir fileName).TrimStart '.'
            copyFile target fileName
            


    module internal PhantomJS = 

        open System.Reflection
        open System.Diagnostics

        type Dummy = class end

        let writeAssemblyResource target fileName = 
            let asm = Assembly.GetAssembly(typeof<Dummy>)
            let file = FileInfo(Path.Combine(target, fileName))
            if file.Exists
            then ()
            else 
                use sr = asm.GetManifestResourceStream("PdfGenerator.tools." + fileName)
                use fs = File.OpenWrite(file.FullName)
                if isNull(sr) 
                then 
                    let availableResources = 
                        String.Join(Environment.NewLine, asm.GetManifestResourceNames())
                    let msg = 
                        sprintf "Unable to find a resource named %A in assembly %A candidates are %s%s"
                            fileName 
                            (asm.GetName().FullName)
                            Environment.NewLine
                            availableResources 
                    raise(ArgumentException(msg, "fileName"))
                else 
                    sr.CopyTo(fs)
                    fs.Flush()
                    
            file

 
        let ensure workingDirectory = 
            let dir = Directory.CreateDirectory(workingDirectory)
            writeAssemblyResource dir.FullName "rasterize.js" |> ignore
            writeAssemblyResource dir.FullName "phantomjs.exe"

        let run (timeout:TimeSpan) workingDirectory f phantomArgs phantomJSScript args =
            let psi = ProcessStartInfo()
            psi.Arguments <- sprintf "%s %s %s" phantomArgs phantomJSScript args
            psi.UseShellExecute <- false
            psi.CreateNoWindow <- false
            psi.FileName <- Path.Combine(workingDirectory,"phantomjs.exe")
            psi.WorkingDirectory <- workingDirectory
            psi.RedirectStandardError <- true 
            psi.RedirectStandardOutput <- true
       

            let p = new Process()
            p.StartInfo <- psi
            f <| LogEntry.Info(sprintf "Running PhantomJS: %s %s" psi.FileName psi.Arguments)
            p.EnableRaisingEvents <- true
      
            p.OutputDataReceived |> Event.add (fun x -> f <| (LogEntry.Info x.Data)) 
            p.ErrorDataReceived |> Event.add (fun x -> f <| (LogEntry.Error x.Data))
            p.Start() |> ignore
            p.BeginOutputReadLine()
            p.BeginErrorReadLine()
            p.WaitForExit(int timeout.TotalMilliseconds) |> ignore

    let createTempCopy temp sourceFile assets = 
        [
            for asset in Seq.append [sourceFile] assets do 
                yield File.copyFileWithSubfolder (Path.GetDirectoryName(sourceFile)) temp asset
        ]

    let fixPhantomJsPaths (path:string) = 
        let path = 
            if Path.IsPathRooted(path)
            then 
                let root = Path.GetPathRoot(path)
                path.Replace(root, "file:///" + root + "/")
            else 
                path
        Path.GetFileName(path.Replace(Path.DirectorySeparatorChar.ToString(), "/"))

    let generatePdf logger workingDirectory inputFile (assets:seq<string>) (outputFile:string) = 
        let workingDir = Directory.CreateDirectory(workingDirectory)
        let pjsPath = PhantomJS.ensure workingDir.FullName

        //Create a temporary copy of the files to get around limitations in PhantomJS's file handling.
        let copiedFiles = createTempCopy pjsPath.Directory.FullName inputFile assets

        try
            let fileToProcess = copiedFiles |> Seq.head
            let generatedFile = (fixPhantomJsPaths outputFile) 

            //Build the PhantomJS Arguments
            let args = sprintf "%s %s A4" (fixPhantomJsPaths fileToProcess.FullName) generatedFile
            let pjsArgs = "--local-to-remote-url-access=true"

            //Run Phantom Js
            PhantomJS.run (TimeSpan.FromMinutes(1.)) workingDir.FullName logger pjsArgs "rasterize.js" args

            let out = Path.Combine(Path.GetDirectoryName(pjsPath.FullName),generatedFile)
            if File.Exists(out)
            then 
                File.Move(out, outputFile)
                Ok (FileInfo outputFile)
            else Error (Exception(sprintf "The output file %A was not created" out))

        finally
            for file in copiedFiles do 
                file.Delete()


    type PdfGeneratorSettings = { 
        WorkingDir:string 
        InputFile:string 
        OutputFile:string
        Assets:seq<string>
        OnLog : (LogEntry -> unit)
        ReplyChannel : AsyncReplyChannel<Result<FileInfo,Exception>>
    }

    [<AllowNullLiteral>]
    type PdfGenerator() = 
        let processor = 
            MailboxProcessor.Start(fun inbox -> 
                async {
                    while true do
                        let! msg = inbox.Receive()
                        try 
                            let result = generatePdf msg.OnLog msg.WorkingDir msg.InputFile msg.Assets msg.OutputFile
                            msg.ReplyChannel.Reply(result)
                        with e -> 
                            msg.ReplyChannel.Reply(Error e)
                }
            )

        member __.Generate(workingDir, inputFile, assets, outputFile, onLog) = 
            processor.PostAndAsyncReply(
               fun r -> 
                { 
                    WorkingDir = workingDir; 
                    InputFile = inputFile; 
                    Assets = assets; 
                    OnLog = onLog; 
                    OutputFile = outputFile;
                    ReplyChannel = r
                })

        



