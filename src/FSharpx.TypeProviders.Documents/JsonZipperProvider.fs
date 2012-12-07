﻿module internal FSharpx.TypeProviders.JsonZipperTypeProvider

open System
open System.IO
open FSharpx.TypeProviders.Helper
open Microsoft.FSharp.Quotations
open Microsoft.FSharp.Core.CompilerServices
open Samples.FSharp.ProvidedTypes
open FSharpx.TypeProviders.Inference
open FSharpx.JSON
open FSharpx.JSON.Zipper
open FSharpx.Strings

open System.Xml
open System.Xml.Linq


let dict = new System.Collections.Generic.Dictionary<_,_>()

/// Generates type for an inferred XML element
let rec generateType (ownerType:ProvidedTypeDefinition) elementName =
    let append =
        match dict.TryGetValue elementName with
        | true,c -> dict.[elementName] <- c + 1; c.ToString()
        | _ -> dict.Add(elementName,1); ""

    let ty = runtimeType<JsonZipper> (elementName + append)
    ownerType.AddMember(ty)
    ty

let createTopF topType (newType:ProvidedTypeDefinition) =
    let toStringMethod =
        ProvidedMethod(
            methodName = "Top",
            parameters = [],
            returnType = topType,
            InvokeCode = (fun args -> <@@ top (%%args.[0]: JsonZipper) @@>))

    toStringMethod.AddXmlDoc "Moves the zipper to the top"

    newType.AddMember toStringMethod


let createSimpleType topType parentType isOptional name propertyType = 
    let newType = generateType parentType name
    newType.HideObjectMethods <- true

    createTopF topType newType
    
    let updateF =
        ProvidedMethod(
            methodName = "Update",
            parameters = [ProvidedParameter("newValue",propertyType)],
            returnType = parentType,
            InvokeCode =
                match propertyType with
                | x when x = typeof<int> -> fun args -> <@@ (%%args.[0]: JsonZipper) |> update (JsonValue.NumDecimal (decimal (%%args.[1]: int))) @@>
                | x when x = typeof<int64> -> fun args -> <@@ (%%args.[0]: JsonZipper) |> update (JsonValue.NumDecimal (decimal (%%args.[1]: int64)))  @@>
                | x when x = typeof<decimal> -> fun args -> <@@ (%%args.[0]: JsonZipper) |> update (JsonValue.NumDecimal (%%args.[1]: decimal)) @@>
                | x when x = typeof<float> -> fun args -> <@@ (%%args.[0]: JsonZipper) |> update (JsonValue.NumDouble (%%args.[1]: float))  @@>
                | x when x = typeof<bool> -> fun args -> <@@ (%%args.[0]: JsonZipper) |> update (JsonValue.Bool (%%args.[1]: bool))  @@>
                | x when x = typeof<DateTime> -> fun args -> <@@ (%%args.[0]: JsonZipper) |> update (JsonValue.String((%%args.[1]: DateTime).ToString()))  @@>
                | x when x = typeof<string> -> fun args -> <@@ (%%args.[0]: JsonZipper) |> update (JsonValue.String (%%args.[1]: string)) @@>)

    updateF.AddXmlDoc (sprintf "Updates the value of the property named \"%s\"." name)

    newType.AddMember updateF    

    let getValueF =
        let accessExpr : (Expr list -> Expr) =
           match propertyType with
            | x when x = typeof<int> -> fun args -> <@@ ((%%args.[0]: JsonZipper) |> focus).GetDecimal() |> int @@>
            | x when x = typeof<int64> -> fun args -> <@@ ((%%args.[0]: JsonZipper) |> focus).GetDecimal() |> int64 @@>
            | x when x = typeof<decimal> -> fun args -> <@@ ((%%args.[0]: JsonZipper) |> focus).GetDecimal() @@>
            | x when x = typeof<float> -> fun args -> <@@ ((%%args.[0]: JsonZipper) |> focus).GetDouble() @@>
            | x when x = typeof<bool> -> fun args -> <@@ ((%%args.[0]: JsonZipper) |> focus).GetBoolean() @@>
            | x when x = typeof<DateTime> -> fun args -> <@@ ((%%args.[0]: JsonZipper) |> focus).GetDate() @@>
            | x when x = typeof<string> -> fun args -> <@@ ((%%args.[0]: JsonZipper) |> focus).GetText() @@>

        if isOptional then
            let optionalType = optionType propertyType
            // For optional elements, we return Option value
            let cases = Reflection.FSharpType.GetUnionCases optionalType
            let some = cases |> Seq.find (fun c -> c.Name = "Some")
            let none = cases |> Seq.find (fun c -> c.Name = "None")

            let checkIfOptional (args: Expr list) = <@@ (%%args.[0]: JsonZipper) |> isFocused @@>

            let optionalAccessExpr =
                (fun args ->
                    Expr.IfThenElse
                        (checkIfOptional args,
                        Expr.NewUnionCase(some, [accessExpr args]),
                        Expr.NewUnionCase(none, [])))

            ProvidedMethod(
                methodName = "GetValue",
                parameters = [],
                returnType = optionalType,
                InvokeCode = optionalAccessExpr)
        else
            ProvidedMethod(
                methodName = "GetValue",
                parameters = [],
                returnType = propertyType,
                InvokeCode = accessExpr)

    getValueF.AddXmlDoc (sprintf "Gets the value of the property named \"%s\"." name)

    newType.AddMember getValueF    

    newType

let createProperty topType parentType isOptional name propertyType = 
    let newType = createSimpleType topType parentType isOptional name propertyType

    let property =
        ProvidedProperty(
            propertyName = niceName name,
            propertyType = newType,
            GetterCode = (fun args -> <@@ (%%args.[0]: JsonZipper) |> toProperty name @@>))

    property.AddXmlDoc (sprintf "Gets the property named \"%s\"" name)
    parentType.AddMember property

let createArrayProperty topType (parentType:ProvidedTypeDefinition) (CompoundProperty(elementName,multi,elementChildren,elementProperties)) =
    let newType = generateType parentType elementName
    newType.HideObjectMethods <- true

    let arrayElementType = generateType newType (elementName + "Element")
        
    let getElementF =
        ProvidedMethod(
            methodName =  "GetElement",
            parameters = [ProvidedParameter("index",typeof<int>)],
            returnType = arrayElementType,
            InvokeCode = fun args -> <@@ (%%args.[0]: JsonZipper) |> down |> moveRight (%%args.[1]:int)|> down @@>)

    getElementF.AddXmlDoc "Gets the element at the specified position."  

    newType.AddMember getElementF

    let getCountF =
        ProvidedMethod(
            methodName =  "GetCount",
            parameters = [],
            returnType = typeof<int>,
            InvokeCode = fun args -> <@@ (%%args.[0]: JsonZipper) |> countSubElements @@>)

    getCountF.AddXmlDoc "Gets the element count"  

    newType.AddMember getCountF
        
    let property =
        ProvidedProperty(
            propertyName = pluralize (niceName elementName),
            propertyType = newType,
            GetterCode = (fun args -> <@@ (%%args.[0]: JsonZipper) |> toProperty elementName @@>))

    property.AddXmlDoc (sprintf "Gets the property named \"%s\"" elementName)
    parentType.AddMember property
    arrayElementType

let createObjectProperty topType (parentType:ProvidedTypeDefinition) (CompoundProperty(elementName,multi,elementChildren,elementProperties)) =
    let newType = generateType parentType elementName
    newType.HideObjectMethods <- true

    let property =
        ProvidedProperty(
            propertyName = niceName elementName,
            propertyType = newType,
            GetterCode = (fun args -> <@@ (%%args.[0]: JsonZipper) |> toProperty elementName |> down @@>))

    property.AddXmlDoc (sprintf "Gets the property named \"%s\"" elementName)
    parentType.AddMember property

    createTopF topType newType

    let upF =
        ProvidedMethod(
            methodName = "Up",
            parameters = [],
            returnType = parentType,
            InvokeCode = (fun args -> <@@ (%%args.[0]: JsonZipper) |> up  @@>))

    upF.AddXmlDoc "Moves the zipper one level up"

    newType.AddMember upF

    newType

let rec generateObj mainLevel topType (parentType:ProvidedTypeDefinition) (CompoundProperty(elementName,multi,elementChildren,elementProperties) as compound)  =
    let typeToModify =
        if mainLevel then parentType else
        if multi then createArrayProperty topType parentType compound
        else createObjectProperty topType parentType compound


    for children in elementChildren do
        generateObj false topType typeToModify children

    for (SimpleProperty(propertyName,propertyType,isOptional)) in elementProperties do
        createProperty topType typeToModify isOptional propertyName propertyType    

/// Infer schema from the loaded data and generate type with properties
let jsonType (ownerType:TypeProviderForNamespaces) (cfg:TypeProviderConfig) =
    let missingValue = "@@@missingValue###"
    let jsonDocumentType = erasedType<obj> thisAssembly rootNamespace "JsonZipper"
    jsonDocumentType.DefineStaticParameters(
        parameters = [ProvidedStaticParameter("FileName", typeof<string>, missingValue)   // Parameterize the type by the file to use as a template
                      ProvidedStaticParameter("Schema" , typeof<string>, missingValue) ], // Allows to specify inlined schema
        instantiationFunction = 
            (fun typeName parameterValues ->
                 
                let schema = 
                    match parameterValues with 
                    | [| :? string as fileName; :? string |] when fileName <> missingValue ->        
                        let resolvedFileName = findConfigFile cfg.ResolutionFolder fileName
                        watchForChanges ownerType resolvedFileName
                                       
                        resolvedFileName |> File.ReadAllText
                    | [| :? string; :? string as schema |] when schema <> missingValue -> schema
                    | _ -> failwith "You have to specify a filename or inlined Schema"
                    

                let parserType = erasedType<JsonZipper> thisAssembly rootNamespace typeName
                parserType.HideObjectMethods <- true
     
                let defaultConstructor = 
                    ProvidedConstructor(
                        parameters = [],
                        InvokeCode = (fun args -> <@@ parse schema |> toZipper @@>))
                defaultConstructor.AddXmlDoc "Initializes the document from the schema sample."

                parserType.AddMember defaultConstructor

                let fileNameConstructor = 
                    ProvidedConstructor(
                        parameters = [ProvidedParameter("filename", typeof<string>)],
                        InvokeCode = (fun args -> <@@ (%%args.[0] : string) |> File.ReadAllText |> parse |> toZipper  @@>))
                fileNameConstructor.AddXmlDoc "Initializes a document from the given path."

                parserType.AddMember fileNameConstructor

                let inlinedDocumentConstructor = 
                    ProvidedConstructor(
                        parameters = [ProvidedParameter("documentContent", typeof<string>)],
                        InvokeCode = (fun args -> <@@ (%%args.[0] : string) |> parse |> toZipper @@>))
                inlinedDocumentConstructor.AddXmlDoc "Initializes a document from the given string."

                parserType.AddMember inlinedDocumentConstructor
                
                let toStringMethod =
                    ProvidedMethod(
                        methodName = "ToString",
                        parameters = [],
                        returnType = typeof<string>,
                        InvokeCode = (fun args -> <@@ (fromZipper (%%args.[0]: JsonZipper)).ToString() @@>))

                toStringMethod.AddXmlDoc "Gets the string representation"

                parserType.AddMember toStringMethod

                let schema = parse schema
                
                generateObj true parserType parserType (JSONInference.provideElement "Document" false [schema])
                |> ignore

                let converterMethod =
                    ProvidedMethod(
                        methodName = "ToXml",
                        parameters = [],
                        returnType = typeof<XObject seq>,
                        InvokeCode = (fun args -> <@@ (fromZipper (%%args.[0]: JsonZipper)).ToXml() @@>))

                converterMethod.AddXmlDoc "Gets the XML representation"

                parserType.AddMember converterMethod
                parserType))
    jsonDocumentType