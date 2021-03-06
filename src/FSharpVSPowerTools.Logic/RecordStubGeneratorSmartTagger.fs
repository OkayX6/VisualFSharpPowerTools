﻿namespace FSharpVSPowerTools.Refactoring

open Microsoft.VisualStudio.Text
open Microsoft.VisualStudio.Text.Editor
open Microsoft.VisualStudio.Text.Tagging
open Microsoft.VisualStudio.Text.Operations
open Microsoft.VisualStudio.Language.Intellisense
open System
open FSharpVSPowerTools
open FSharpVSPowerTools.CodeGeneration
open FSharpVSPowerTools.CodeGeneration.RecordStubGenerator
open FSharpVSPowerTools.ProjectSystem
open Microsoft.FSharp.Compiler.SourceCodeServices

type RecordStubGeneratorSmartTag(actionSets) =
    inherit SmartTag(SmartTagType.Factoid, actionSets)

type RecordStubGenerator
    (
        textDocument: ITextDocument,
        view: ITextView,
        textUndoHistory: ITextUndoHistory,
        vsLanguageService: VSLanguageService,
        projectFactory: ProjectFactory,
        defaultBody: string,
        openDocumentTracker: IOpenDocumentsTracker
    ) as self =

    let changed = Event<_>()
    let mutable currentWord: SnapshotSpan option = None
    let mutable suggestions: ISuggestion list = []
    
    let buffer = view.TextBuffer
    let codeGenService: ICodeGenerationService<_, _, _> = upcast CodeGenerationService(vsLanguageService, buffer)

    // Check whether the record has been fully defined
    let shouldGenerateRecordStub (recordExpr: RecordExpr) (entity: FSharpEntity) =
        let fieldCount = entity.FSharpFields.Count
        let writtenFieldCount = recordExpr.FieldExprList.Length
        fieldCount > 0 && writtenFieldCount < fieldCount

    let handleGenerateRecordStub (snapshot: ITextSnapshot) (recordExpr: RecordExpr) (insertionPos: _) entity = 
        let fieldsWritten = recordExpr.FieldExprList

        use transaction = textUndoHistory.CreateTransaction(Resource.recordGenerationCommandName)

        let stub = RecordStubGenerator.formatRecord
                       insertionPos
                       defaultBody
                       entity
                       fieldsWritten
        let currentLine = snapshot.GetLineFromLineNumber(insertionPos.InsertionPos.Line-1).Start.Position + insertionPos.InsertionPos.Column

        buffer.Insert(currentLine, stub) |> ignore

        transaction.Complete()

    let getSuggestions (recordExpr, entity, insertionParams, snapshot) =
        [ 
            { new ISuggestion with
                  member __.Invoke() = handleGenerateRecordStub snapshot recordExpr insertionParams entity
                  member __.NeedsIcon = false
                  member __.Text = Resource.recordGenerationCommandName }
        ]

    let project = lazy (projectFactory.CreateForDocument buffer textDocument.FilePath)

    // Try to:
    // - Identify record expression binding
    // - Identify the '{' in 'let x: MyRecord = { }'
    let updateAtCaretPosition (CallInUIContext callInUIContext) =
        async {
            match buffer.GetSnapshotPoint view.Caret.Position, currentWord with
            | Some point, Some word when word.Snapshot = view.TextSnapshot && point.InSpan word -> return ()
            | (Some _ | None), _ ->
                let! result = asyncMaybe {
                    let! point = buffer.GetSnapshotPoint view.Caret.Position
                    let! project = project.Value
                    let! word, _ = vsLanguageService.GetSymbol (point, textDocument.FilePath, project) 
                    
                    do! match currentWord with
                        | None -> Some()
                        | Some oldWord -> 
                            if word <> oldWord then Some()
                            else None

                    currentWord <- Some word
                    suggestions <- []
                    let! source = openDocumentTracker.TryGetDocumentText textDocument.FilePath
                    let vsDocument = VSDocument(source, textDocument.FilePath, point.Snapshot)
                    let! symbolRange, recordExpression, recordDefinition, insertionPos =
                        tryFindRecordDefinitionFromPos codeGenService project point vsDocument
                    // Recheck cursor position to ensure it's still in new word
                    let! point = buffer.GetSnapshotPoint view.Caret.Position
                    if point.InSpan symbolRange && shouldGenerateRecordStub recordExpression recordDefinition then
                        return! Some (recordExpression, recordDefinition, insertionPos, word.Snapshot)
                    else
                        return! None
                } 
                suggestions <- result |> Option.map getSuggestions |> Option.getOrElse []
                do! callInUIContext <| fun _ -> changed.Trigger self
        }

    let docEventListener = new DocumentEventListener ([ViewChange.layoutEvent view; ViewChange.caretEvent view], 
                                                      500us, updateAtCaretPosition)
    member __.Changed = changed.Publish
    member __.CurrentWord = 
        currentWord |> Option.map (fun word ->
            if buffer.CurrentSnapshot = word.Snapshot then word
            else word.TranslateTo(buffer.CurrentSnapshot, SpanTrackingMode.EdgeExclusive))
    member __.Suggestions = suggestions

    interface IDisposable with
        member __.Dispose() = 
            (docEventListener :> IDisposable).Dispose()

type RecordStubGeneratorSmartTagger(buffer: ITextBuffer, generator: RecordStubGenerator) as self =
    let tagsChanged = Event<_,_>()
    do generator.Changed.Add (fun _ -> buffer.TriggerTagsChanged self tagsChanged)
    interface ITagger<RecordStubGeneratorSmartTag> with
        member __.GetTags(_spans: NormalizedSnapshotSpanCollection): ITagSpan<RecordStubGeneratorSmartTag> seq =
            protectOrDefault (fun _ ->
                seq {
                    match generator.CurrentWord, generator.Suggestions with
                    | None, _
                    | _, [] -> ()
                    | Some word, suggestions ->
                        let actions =
                            suggestions
                            |> List.map (fun s ->
                                 { new ISmartTagAction with
                                     member __.ActionSets = null
                                     member __.DisplayText = s.Text
                                     member __.Icon = null
                                     member __.IsEnabled = true
                                     member __.Invoke() = s.Invoke() })
                            |> Seq.toReadOnlyCollection
                            |> fun xs -> [ SmartTagActionSet xs ]
                            |> Seq.toReadOnlyCollection
                        yield TagSpan<_>(word, RecordStubGeneratorSmartTag(actions)) :> _ })
                Seq.empty

        [<CLIEvent>]
        member __.TagsChanged = tagsChanged.Publish

    interface IDisposable with
        member __.Dispose() = 
            (generator :> IDisposable).Dispose()
