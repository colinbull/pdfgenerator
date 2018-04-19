 module GeneratePdfExample = 

            open System.IO
            open PdfGenerator

            module Razor = 

                open RazorLight
                open System.Collections.Concurrent

                let private engines = ConcurrentDictionary<string, RazorLightEngine>()
                
                let getEngine basePath = 
                    engines.GetOrAdd(basePath, fun key -> 
                        RazorLightEngineBuilder()
                        |> fun x -> x.UseFileSystemProject(key)
                        |> fun x -> x.UseMemoryCachingProvider()
                        |> fun x -> x.Build()
                    )

                let compileHtmlString<'a> basePath templatePath model = 
                    async { 
                        let engine = getEngine basePath
                        let! result = engine.CompileRenderAsync<'a>(templatePath, model) |> Async.AwaitTask
                        return result
                    }

            let private generator = PdfGenerator() 

            let writeFile path (data:string) = async {
                use fs = File.OpenWrite(path)
                use sw = new StreamWriter(fs) 
                do! sw.WriteAsync(data) |> Async.AwaitTask
                do! sw.FlushAsync() |> Async.AwaitTask
            }

            let prepareFiles (workingDir:DirectoryInfo) baseDir template (model:'a) = async {
                let! htmlString = Razor.compileHtmlString baseDir template model

                let fileName = Path.GetFileNameWithoutExtension(template) + "_" + typeof<'a>.Name
                let htmlFile = Path.Combine(workingDir.FullName, Path.ChangeExtension(fileName, ".html"))
                let pdfFile = Path.Combine(workingDir.FullName, Path.ChangeExtension(fileName, ".pdf"))

                do! writeFile htmlFile htmlString
                return pdfFile, htmlFile
            }

            let writePdf (outputDirectories:DirectoryInfo[]) tempDir baseDir outputFile template assets (report:'a) =
                let name = "Pdf Report Writer"
                async {   
                    if outputDirectories.Length > 0
                    then
                        try
                            let workingDir = Directory.CreateDirectory(tempDir)

                            let! (pdfFile, htmlFile) = prepareFiles workingDir baseDir template report 
                            return generator.Generate(workingDir.FullName,
                                                                     htmlFile,
                                                                     assets,
                                                                     pdfFile, 
                                                                     function 
                                                                     | PdfGenerator.LogEntry.Info s -> printfn "%s" s
                                                                     | PdfGenerator.LogEntry.Error s -> eprintfn "%s" s) 
                        with e -> 
                            return Error [name, e]
                    else 
                        return Ok None
                }