module Tests

open System
open Expecto
open Npgsql.FSharp.Analyzers
open Npgsql.FSharp.Analyzers.Core
open Npgsql.FSharp
open ThrowawayDb.Postgres

let analyzers = [
    SqlAnalyzer.queryAnalyzer
]

let inline find file = IO.Path.Combine(__SOURCE_DIRECTORY__ , file)
let project = IO.Path.Combine(__SOURCE_DIRECTORY__, "../examples/hashing/examples.fsproj")

let inline context file =
    AnalyzerBootstrap.context file
    |> Option.map SqlAnalyzer.sqlAnalyzerContext

let createTestDatabase() =
    Sql.host "localhost"
    |> Sql.port 5432
    |> Sql.username "postgres"
    |> Sql.password "postgres"
    |> Sql.formatConnectionString
    |> ThrowawayDatabase.Create

[<Tests>]
let tests =
    testList "Postgres" [
        test "Syntactic Analysis: SQL blocks can be detected with their relavant information" {
            match context (find "../examples/hashing/syntacticAnalysis.fs") with
            | None -> failwith "Could not crack project"
            | Some context ->
                let operationBlocks = SyntacticAnalysis.findSqlOperations context
                Expect.equal 10 (List.length operationBlocks) "Found ten operation blocks"
        }

        test "Syntactic analysis: no SQL blocks should be found using sprintf" {
            match context (find "../examples/hashing/SprintfBlock.fs") with
            | None -> failwith "Could not crack project"
            | Some context ->
                let operations = SyntacticAnalysis.findSqlOperations context
                Expect.isEmpty operations "There should be no syntactic blocks"
        }

        test "Syntactic Analysis: reading queries with [<Literal>] query" {
            match context (find "../examples/hashing/syntacticAnalysis-literalStrings.fs") with
            | None -> failwith "Could not crack project"
            | Some context ->
                match SyntacticAnalysis.findSqlOperations context with
                | [ operation ] ->
                    match SqlAnalysis.findQuery operation with
                    | Some(query, range) -> Expect.equal query "SELECT * FROM users" "Literal string should be read correctly"
                    | None -> failwith "Should have found the correct query"
                | _ ->
                    failwith "Should not happen"
        }

        test "Syntactic Analysis: simple queries can be read" {
            match context (find "../examples/hashing/syntacticAnalysisSimpleQuery.fs") with
            | None -> failwith "Could not crack project"
            | Some context ->
                match SyntacticAnalysis.findSqlOperations context with
                | [ operation ] ->
                    match SqlAnalysis.findQuery operation with
                    | Some(query, range) -> Expect.equal query "SELECT COUNT(*) FROM users" "Literal string should be read correctly"
                    | None -> failwith "Should have found the correct query"
                | _ ->
                    failwith "Should not happen"
        }

        test "Syntactic Analysis: combination with Sql functions can be detected" {
            match context (find "../examples/hashing/syntacticAnalysisExecuteScalar.fs") with
            | None -> failwith "Could not crack project"
            | Some context ->
                match SyntacticAnalysis.findSqlOperations context with
                | [ operation ] ->
                    match SqlAnalysis.findQuery operation with
                    | Some(query, range) ->
                        Expect.equal query "SELECT COUNT(*) as Count FROM users WHERE is_active = @is_active" "Literal string should be read correctly"
                        match SqlAnalysis.findParameters operation with
                        | Some ([ parameter ], range) ->
                            Expect.equal "is_active" parameter.name "Parameter is correct"
                        | otherwise ->
                            failwith "There should have been a parameter"
                    | None ->
                        failwith "Should have found the correct query"
                | _ ->
                    failwith "Should not happen"
        }

        test "Semantic Analysis: parameter type mismatch" {
            use db = createTestDatabase()

            Sql.connect db.ConnectionString
            |> Sql.query "CREATE TABLE users (user_id bigserial primary key, username text not null, active bit not null)"
            |> Sql.executeNonQuery
            |> ignore

            match context (find "../examples/hashing/parameterTypeMismatch.fs") with
            | None -> failwith "Could not crack project"
            | Some context ->
                match SqlAnalysis.databaseSchema db.ConnectionString with
                | Result.Error connectionError ->
                    failwith connectionError
                | Result.Ok schema ->
                    let block = List.exactlyOne (SyntacticAnalysis.findSqlOperations context)
                    let messages = SqlAnalysis.analyzeOperation block db.ConnectionString schema
                    match messages with
                    | [ message ] ->
                        Expect.isTrue (message.IsWarning()) "The message is an warning"
                        Expect.stringContains message.Message "Sql.int64" "Message should contain the missing column name"
                    | _ ->
                        failwith "Expected only one error message"
        }

        test "Syntactic Analysis: reading queries with extra processing after Sql.executeReader" {
            match context (find "../examples/hashing/syntacticAnalysisProcessedList.fs") with
            | None -> failwith "Could not crack project"
            | Some context ->
                match SyntacticAnalysis.findSqlOperations context with
                | [ operation ] ->
                    match SqlAnalysis.findQuery operation with
                    | Some(query, range) ->
                        Expect.equal query "SELECT * FROM users" "Query should be read correctly"
                        match SqlAnalysis.findColumnReadAttempts operation with
                        | Some [ attempt ] ->
                            Expect.equal attempt.funcName "read.text" "Function name is correct"
                            Expect.equal attempt.columnName "username" "Column name is read correctly"
                        | otherwise ->
                            failwith "Should have found one column read attempt"
                    | None ->
                        failwith "Should have found the correct query"
                | otherwise ->
                    failwith "Should not happen"
        }

        test "SQL schema analysis" {
            use db = createTestDatabase()

            Sql.connect db.ConnectionString
            |> Sql.query "CREATE TABLE users (user_id bigserial primary key, username text not null, active bit not null)"
            |> Sql.executeNonQuery
            |> ignore

            let databaseMetadata = InformationSchema.getDbSchemaLookups db.ConnectionString

            let userColumns =
                databaseMetadata.Schemas.["public"].Tables
                |> Seq.tryFind (fun pair -> pair.Key.Name = "users")
                |> Option.map (fun pair -> pair.Value)
                |> Option.map List.ofSeq

            match userColumns with
            | None ->
                failwith "Expected to find columns for users table"
            | Some columns ->
                Expect.equal 3 (List.length columns) "There are three columns"
        }

        test "SQL schema analysis with arrays" {
            use db = createTestDatabase()

            Sql.connect db.ConnectionString
            |> Sql.query "CREATE TABLE users (user_id bigserial primary key, roles text[] not null)"
            |> Sql.executeNonQuery
            |> ignore

            let databaseMetadata = InformationSchema.getDbSchemaLookups db.ConnectionString

            let userColumns =
                databaseMetadata.Schemas.["public"].Tables
                |> Seq.tryFind (fun pair -> pair.Key.Name = "users")
                |> Option.map (fun pair -> pair.Value)
                |> Option.map List.ofSeq

            match userColumns with
            | None ->
                failwith "Expected to find columns for users table"
            | Some columns ->
                Expect.equal 2 (List.length columns) "There are three columns"
                let rolesColumn = columns |> List.find (fun column -> column.Name = "roles")
                Expect.equal rolesColumn.DataType.Name "text" "The data type is text"
                Expect.isTrue rolesColumn.DataType.IsArray "The data type is an array"
                Expect.isFalse rolesColumn.Nullable "The column is not nullable"
        }

        test "SQL query analysis" {
            use db = createTestDatabase()

            Sql.connect db.ConnectionString
            |> Sql.query "CREATE TABLE users (user_id bigserial primary key, username text not null, active bit not null)"
            |> Sql.executeNonQuery
            |> ignore

            let databaseMetadata = InformationSchema.getDbSchemaLookups db.ConnectionString

            let query = "SELECT * FROM users"
            let parameters, outputColumns, enums = InformationSchema.extractParametersAndOutputColumns(db.ConnectionString, query, false, databaseMetadata)
            Expect.isEmpty parameters "Query contains no parameters"
            Expect.equal 3 (List.length outputColumns) "There are 3 columns in users table"
        }

        test "SQL scalar query analysis" {
            use db = createTestDatabase()

            Sql.connect db.ConnectionString
            |> Sql.query "CREATE TABLE users (user_id bigserial primary key, username text not null, active bit not null)"
            |> Sql.executeNonQuery
            |> ignore

            let databaseMetadata = InformationSchema.getDbSchemaLookups db.ConnectionString
            let query = "SELECT COUNT(*) FROM users"
            let resultSetMetadata = SqlAnalysis.extractParametersAndOutputColumns(db.ConnectionString, query, databaseMetadata)
            match resultSetMetadata with
            | Result.Error errorMsg ->
                failwithf "Could not analyse result set metadata %s" errorMsg
            | Result.Ok (parameters, outputColumns) ->
                Expect.isEmpty parameters "Query shouldn't contain any parameters"
                Expect.equal 1 outputColumns.Length "There is one column returned"
        }

        test "SQL function analysis" {
            use db = createTestDatabase()

            let createFuncQuery = """
            CREATE FUNCTION Increment(val integer) RETURNS integer AS $$
            BEGIN
            RETURN val + 1;
            END; $$
            LANGUAGE PLPGSQL;
            """

            Sql.connect db.ConnectionString
            |> Sql.query createFuncQuery
            |> Sql.executeNonQuery
            |> ignore

            let databaseMetadata = InformationSchema.getDbSchemaLookups db.ConnectionString
            let query = "SELECT Increment(@Input)"
            let resultSetMetadata = SqlAnalysis.extractParametersAndOutputColumns(db.ConnectionString, query, databaseMetadata)
            match resultSetMetadata with
            | Result.Error errorMsg ->
                failwithf "Could not analyse result set metadata %s" errorMsg
            | Result.Ok (parameters, outputColumns) ->
                Expect.equal 1 parameters.Length "Query has one parameter"
                Expect.equal "integer" parameters.[0].DataType.Name "The parameter is int4"
                Expect.equal 1 outputColumns.Length "There is one column returned"
                Expect.equal "integer" outputColumns.[0].DataType.Name "The output type is int4"
        }

        test "SQL query semantic analysis: missing column" {
            use db = createTestDatabase()

            Sql.connect db.ConnectionString
            |> Sql.query "CREATE TABLE users (user_id bigserial primary key, username text not null, active bit not null)"
            |> Sql.executeNonQuery
            |> ignore

            match context (find "../examples/hashing/semanticAnalysis-missingColumn.fs") with
            | None -> failwith "Could not crack project"
            | Some context ->
                match SqlAnalysis.databaseSchema db.ConnectionString with
                | Result.Error connectionError ->
                    failwith connectionError
                | Result.Ok schema ->
                    let block = List.exactlyOne (SyntacticAnalysis.findSqlOperations context)
                    let messages = SqlAnalysis.analyzeOperation block db.ConnectionString schema
                    match messages with
                    | [ message ] ->
                        Expect.isTrue (message.IsWarning()) "The message is an warning"
                        Expect.stringContains message.Message "non_existent" "Message should contain the missing column name"
                    | _ ->
                        failwith "Expected only one error message"
        }

        test "SQL query semantic analysis: missing parameter" {
            use db = createTestDatabase()

            Sql.connect db.ConnectionString
            |> Sql.query "CREATE TABLE users (user_id bigserial primary key, username text not null, active bit not null)"
            |> Sql.executeNonQuery
            |> ignore

            match context (find "../examples/hashing/semanticAnalysis-missingParameter.fs") with
            | None -> failwith "Could not crack project"
            | Some context ->
                let block = List.exactlyOne (SyntacticAnalysis.findSqlOperations context)
                match SqlAnalysis.databaseSchema db.ConnectionString with
                | Result.Error connectionError ->
                    failwith connectionError
                | Result.Ok schema ->
                    let messages = SqlAnalysis.analyzeOperation block db.ConnectionString schema
                    match messages with
                    | [ message ] ->
                        Expect.isTrue (message.IsWarning()) "The message is a warning"
                        Expect.stringContains message.Message "Missing parameter 'active'"  "Error should say which parameter is not provided"
                    | _ ->
                        failwith "Expected only one error message"
        }

        test "SQL query semantic analysis: type mismatch" {
            use db = createTestDatabase()

            Sql.connect db.ConnectionString
            |> Sql.query "CREATE TABLE users (user_id bigserial primary key, username text not null, active bit not null)"
            |> Sql.executeNonQuery
            |> ignore

            match context (find "../examples/hashing/semanticAnalysis-typeMismatch.fs") with
            | None -> failwith "Could not crack project"
            | Some context ->
                match SqlAnalysis.databaseSchema db.ConnectionString with
                | Result.Error connectionError ->
                    failwith connectionError
                | Result.Ok schema ->
                    let operation = List.exactlyOne (SyntacticAnalysis.findSqlOperations context)
                    let messages = SqlAnalysis.analyzeOperation operation db.ConnectionString schema
                    match messages with
                    | [ message ] ->
                        Expect.stringContains message.Message "Please use read.bool instead" "Message contains suggestion to use Sql.readBool"
                    | _ ->
                        failwith "Expected only one error message"
        }

        test "SQL query semantic analysis: type mismatch when using text[]" {
            use db = createTestDatabase()

            Sql.connect db.ConnectionString
            |> Sql.query "CREATE TABLE users (user_id bigserial primary key, roles text[] not null)"
            |> Sql.executeNonQuery
            |> ignore

            match context (find "../examples/hashing/readingTextArray.fs") with
            | None -> failwith "Could not crack project"
            | Some context ->
                match SqlAnalysis.databaseSchema db.ConnectionString with
                | Result.Error connectionError ->
                    failwith connectionError
                | Result.Ok schema ->
                    let operation = List.exactlyOne (SyntacticAnalysis.findSqlOperations context)
                    let messages = SqlAnalysis.analyzeOperation operation db.ConnectionString schema
                    match messages with
                    | [ message ] ->
                        Expect.stringContains message.Message "Please use read.stringArray instead" "Message contains suggestion to use Sql.stringArray"
                    | _ ->
                        failwith "Expected only one error message"
        }

        test "SQL query semantic analysis: type mismatch when using uuid[]" {
            use db = createTestDatabase()

            Sql.connect db.ConnectionString
            |> Sql.query "CREATE TABLE users (user_id bigserial primary key, codes uuid[] not null)"
            |> Sql.executeNonQuery
            |> ignore

            match context (find "../examples/hashing/readingUuidArray.fs") with
            | None -> failwith "Could not crack project"
            | Some context ->
                match SqlAnalysis.databaseSchema db.ConnectionString with
                | Result.Error connectionError ->
                    failwith connectionError
                | Result.Ok schema ->
                    let operation = List.exactlyOne (SyntacticAnalysis.findSqlOperations context)
                    let messages = SqlAnalysis.analyzeOperation operation db.ConnectionString schema
                    match messages with
                    | [ message ] ->
                        Expect.stringContains message.Message "Please use read.uuidArray instead" "Message contains suggestion to use Sql.stringArray"
                    | _ ->
                        failwith "Expected only one error message"
        }

        test "SQL query semantic analysis: type mismatch when using int[] as parameter" {
            use db = createTestDatabase()

            Sql.connect db.ConnectionString
            |> Sql.query "CREATE TABLE users (user_id bigserial primary key, username text)"
            |> Sql.executeNonQuery
            |> ignore

            match context (find "../examples/hashing/usingIntArrayParameter.fs") with
            | None -> failwith "Could not crack project"
            | Some context ->
                match SqlAnalysis.databaseSchema db.ConnectionString with
                | Result.Error connectionError ->
                    failwith connectionError
                | Result.Ok schema ->
                    let operation = List.exactlyOne (SyntacticAnalysis.findSqlOperations context)
                    let messages = SqlAnalysis.analyzeOperation operation db.ConnectionString schema
                    match messages with
                    | [ message ] ->
                        Expect.stringContains message.Message "Sql.intArray" "Message contains suggestion to use Sql.stringArray"
                    | _ ->
                        failwith "Expected only one error message"
        }

        test "SQL query semantic analysis: detect incorrectly used Sql.execute where as Sql.executeNonQuery was needed" {
            use db = createTestDatabase()

            Sql.connect db.ConnectionString
            |> Sql.query "CREATE TABLE users (user_id bigserial primary key, username text, roles text[] not null)"
            |> Sql.executeNonQuery
            |> ignore

            match context (find "../examples/hashing/executingQueryInsteadOfNonQuery.fs") with
            | None -> failwith "Could not crack project"
            | Some context ->
                match SqlAnalysis.databaseSchema db.ConnectionString with
                | Result.Error connectionError ->
                    failwith connectionError
                | Result.Ok schema ->
                    let operation = List.exactlyOne (SyntacticAnalysis.findSqlOperations context)
                    let messages = SqlAnalysis.analyzeOperation operation db.ConnectionString schema
                    match messages with
                    | [ message ] ->
                        Expect.stringContains message.Message "Sql.executeNonQuery" "Message contains suggestion to use Sql.stringArray"
                    | _ ->
                        failwith "Expected only one error message"
        }

        test "SQL query semantic analysis: type mismatch with integer/serial" {
            use db = createTestDatabase()

            Sql.connect db.ConnectionString
            |> Sql.query "CREATE TABLE users (user_id serial primary key, username text not null, active bit not null)"
            |> Sql.executeNonQuery
            |> ignore

            match context (find "../examples/hashing/readAttemptIntegerTypeMismatch.fs") with
            | None -> failwith "Could not crack project"
            | Some context ->
                match SqlAnalysis.databaseSchema db.ConnectionString with
                | Result.Error connectionError ->
                    failwith connectionError
                | Result.Ok schema ->
                    let operation = List.exactlyOne (SyntacticAnalysis.findSqlOperations context)
                    let messages = SqlAnalysis.analyzeOperation operation db.ConnectionString schema
                    match messages with
                    | [ message ] ->
                        Expect.stringContains message.Message "read.int" "Message contains suggestion to use Sql.readBool"
                    | _ ->
                        failwith "Expected only one error message"
        }

        test "SQL query semantic analysis: redundant parameters" {
            use db = createTestDatabase()

            Sql.connect db.ConnectionString
            |> Sql.query "CREATE TABLE users (user_id bigserial primary key, username text not null, active bit not null)"
            |> Sql.executeNonQuery
            |> ignore

            match context (find "../examples/hashing/semanticAnalysis-redundantParameters.fs") with
            | None -> failwith "Could not crack project"
            | Some context ->
                match SqlAnalysis.databaseSchema db.ConnectionString with
                | Result.Error connectionError ->
                    failwith connectionError
                | Result.Ok schema ->
                    let block = List.exactlyOne (SyntacticAnalysis.findSqlOperations context)
                    let messages = SqlAnalysis.analyzeOperation block db.ConnectionString schema
                    match messages with
                    | [ message ] ->
                        Expect.stringContains message.Message "Provided parameters are redundant" "Message contains suggestion to remove Sql.parameters"
                    | _ ->
                        failwith "Expected only one error message"
        }
    ]
