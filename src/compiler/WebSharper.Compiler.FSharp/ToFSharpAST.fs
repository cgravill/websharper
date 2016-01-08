﻿module internal WebSharper.Compiler.FSharp.ToFSharpAST

//open System.Runtime.CompilerServices

open Microsoft.FSharp.Compiler.SourceCodeServices
 
open WebSharper.Core
open WebSharper.Core.AST
 
type VarKind =
    | LocalVar 
    | ByRefArg
    | ThisArg
         
type Environment =
    {
        ScopeIds : list<FSharpMemberOrFunctionOrValue * Id * VarKind>
        TParams : Map<string, int>
        Exception : option<Id>
        MatchVars : option<Id * Id>
        Compilation : Metadata.Compilation
    }
    static member New(vars, tparams, comp) = 
        { 
            ScopeIds = vars |> Seq.map (fun (i, (v, k)) -> i, v, k) |> List.ofSeq 
            TParams = tparams |> Seq.mapi (fun i p -> p, i) |> Map.ofSeq
            Exception = None
            MatchVars = None
            Compilation = comp
        }

    member this.WithTParams tparams =
        if List.isEmpty tparams then this else
        { this with 
            TParams = 
                ((this.TParams, this.TParams.Count), tparams) 
                ||> List.fold (fun (m, i) p -> m |> Map.add p i, i + 1) 
                |> fst
        }

    member this.WithVar(i: Id, v: FSharpMemberOrFunctionOrValue, ?k) =
        { this with ScopeIds = (v, i, defaultArg k LocalVar) :: this.ScopeIds }

    member this.WithException (i: Id, v: FSharpMemberOrFunctionOrValue) =
        { this with 
            ScopeIds = (v, i, LocalVar) :: this.ScopeIds
            Exception = Some i }

    member this.LookupVar (v: FSharpMemberOrFunctionOrValue) =
        match this.ScopeIds |> List.tryPick (fun (sv, i, k) -> if sv = v then Some (i, k) else None) with
        | Some var -> var
        | _ -> failwithf "Variable lookup failed: %s" v.DisplayName

let rec getOrigDef (td: FSharpEntity) =
    if td.IsFSharpAbbreviation then getOrigDef td.AbbreviatedType.TypeDefinition else td 

let mutable thisAssemblyName = "CurrentAssembly"

let getSimpleName (a: FSharpAssembly) =
    match a.FileName with
    | None -> thisAssemblyName
    | _ -> a.SimpleName

module M = WebSharper.Core.Metadata

let getTypeDefinition (td: FSharpEntity) =
    if td.IsArrayType then
        TypeDefinition {
            Assembly = "mscorlib"
            FullName = "System.Array`1"
        }
    else
    let td = getOrigDef td
    let res =
        {
            Assembly = getSimpleName td.Assembly 
            FullName = if td.IsProvidedAndErased then td.LogicalName else td.QualifiedName.Split([|','|]).[0] 
        }
    // TODO: more measure types
    match res.Assembly with
    | "FSharp.Core" ->
        match res.FullName with
        | "Microsoft.FSharp.Core.byte`1" -> 
            { Assembly = "mscorlib"; FullName = "System.Byte" }   
        | "Microsoft.FSharp.Core.syte`1" -> 
            { Assembly = "mscorlib"; FullName = "System.SByte" }   
        | "Microsoft.FSharp.Core.int16`1" -> 
            { Assembly = "mscorlib"; FullName = "System.Int16" }   
        | "Microsoft.FSharp.Core.int`1" -> 
            { Assembly = "mscorlib"; FullName = "System.Int32" }   
        | "Microsoft.FSharp.Core.uint16`1" ->
            { Assembly = "mscorlib"; FullName = "System.UInt16" }   
        | "Microsoft.FSharp.Core.uint32`1" -> 
            { Assembly = "mscorlib"; FullName = "System.UInt32" }   
        | "Microsoft.FSharp.Core.decimal`1" -> 
            { Assembly = "mscorlib"; FullName = "System.Decimal" }   
        | "Microsoft.FSharp.Core.int64`1" -> 
            { Assembly = "mscorlib"; FullName = "System.Int64" }   
        | "Microsoft.FSharp.Core.uint64`1" -> 
            { Assembly = "mscorlib"; FullName = "System.UInt64" }   
        | "Microsoft.FSharp.Core.float32`1" ->
            { Assembly = "mscorlib"; FullName = "System.Single" }   
        | "Microsoft.FSharp.Core.float`1" ->
            { Assembly = "mscorlib"; FullName = "System.Double" }   
        | _ -> res
    | _ -> res
    |> fun x -> TypeDefinition x

module A = WebSharper.Compiler.AttributeReader

type FSharpAttributeReader() =
    inherit A.AttributeReader<FSharpAttribute>()
    override this.GetAssemblyName attr = getSimpleName attr.AttributeType.Assembly
    override this.GetName attr = attr.AttributeType.LogicalName
    override this.GetCtorArgs attr = attr.ConstructorArguments |> Seq.map snd |> Array.ofSeq          
    override this.GetTypeDef o = getTypeDefinition (o :?> FSharpType).TypeDefinition

let attrReader = FSharpAttributeReader()

let rec getOrigType (t: FSharpType) =
    if t.IsAbbreviation then getOrigType t.AbbreviatedType else t

//let funcDef =
//    Hashed {
//        Assembly = "FSharp.Core"
//        FullName = "Microsoft.FSharp.Core.FSharpFunc`2"
//    }

let isUnit (t: FSharpType) =
    if t.IsGenericParameter then
        false
    else
    let t = getOrigType t
    if t.IsTupleType || t.IsFunctionType then false else
    let td = t.TypeDefinition
    if td.IsArrayType || td.IsByRef then false
    elif td.IsProvidedAndErased then false
    else td.FullName = "Microsoft.FSharp.Core.Unit" || td.FullName = "System.Void"

let isOption (t: FSharpType) =
    let td = (getOrigType t).TypeDefinition
    not td.IsProvidedAndErased && td.FullName.StartsWith "Microsoft.FSharp.Core.FSharpOption`1"

let isByRef (t: FSharpType) =
    if t.IsGenericParameter then
        false
    else
    let t = getOrigType t
    if t.IsTupleType || t.IsFunctionType then false else
    t.TypeDefinition.IsByRef

exception ParseError of string

let parsefailf x =
    Printf.kprintf (fun s -> raise <| ParseError s) x

let rec getType (tparams: Map<string, int>) (t: FSharpType) =
    if t.IsGenericParameter then
        match tparams.TryFind t.GenericParameter.Name with
        | Some i -> GenericType i
        | _ -> parsefailf "Failed to resolve generic parameter: %s" t.GenericParameter.Name
    else
    let t = getOrigType t
    let getFunc() =
        match t.GenericArguments |> Seq.map (getType tparams) |> List.ofSeq with
        | [a; r] -> FSharpFuncType(a, r)
        | _ -> failwith "impossible: FSharpFunc must have 2 type parameters"
    if t.IsTupleType then
        t.GenericArguments |> Seq.map (getType tparams) |> List.ofSeq |> TupleType
    elif t.IsFunctionType then
//        concreteType(funcDef, t.GenericArguments |> Seq.map (getType tparams) |> List.ofSeq)
        getFunc()
    else
    let td = t.TypeDefinition
    if td.IsArrayType then
        ArrayType(getType tparams t.GenericArguments.[0], 1) // TODO: multi dimensional arrays   
    elif td.IsByRef then
        ByRefType(getType tparams t.GenericArguments.[0])
    else
//        let td = getTypeDefinition td
        let fn = 
            if td.IsProvidedAndErased then td.LogicalName else td.FullName
        if fn.StartsWith "System.Tuple" then
            t.GenericArguments |> Seq.map (getType tparams) |> List.ofSeq |> TupleType
        elif fn = "Microsoft.FSharp.Core.FSharpFunc`2" then
            getFunc()
        elif fn = "Microsoft.FSharp.Core.Unit" || fn = "System.Void" then
            VoidType
        else
            let td = getTypeDefinition td
            // erase Measure parameters
            match td.Value.FullName with
            | "System.Byte" 
            | "System.SByte"            
            | "System.Int16" 
            | "System.Int32" 
            | "System.UInt16" 
            | "System.UInt32" 
            | "System.Decimal"
            | "System.Int64" 
            | "System.UInt64" 
            | "System.Single" 
            | "System.Double" -> concreteType (td, [])  
            | _ ->
                concreteType (td, t.GenericArguments |> Seq.map (getType tparams) |> List.ofSeq)

let removeUnitParam (ps: list<Type>) =
    match ps with 
    | [ VoidType ] -> []
    //| [ ConcreteType { Entity = t } ] when t.Value.FullName.StartsWith "Microsoft.FSharp.Core.Unit" -> []
    | _ -> ps

//let isUnionUsingNull (x: FSharpEntity) =
//    x.IsFSharpUnion && (
//        x.Attributes |> Seq.exists (fun a ->
//            a.AttributeType.FullName = "Microsoft.FSharp.Core.CompilationRepresentationAttribute"
//            && obj.Equals(snd a.ConstructorArguments.[0], int CompilationRepresentationFlags.UseNullAsTrueValue) //
//        )
//    )  
//    <CompilationRepresentation(CompilationRepresentationFlags.Instance)

let hasCompilationRepresentation (cr: CompilationRepresentationFlags) attrs =
    attrs |> Seq.exists (fun (a: FSharpAttribute) ->
        a.AttributeType.FullName = "Microsoft.FSharp.Core.CompilationRepresentationAttribute"
        && obj.Equals(snd a.ConstructorArguments.[0], int cr)
    )

let makeByref getVal setVal =
    let value = Id.New "v"
    Object [
        "get", (Function ([], Return getVal))
        "set", (Function ([value], ExprStatement (setVal (Var value))))
    ]

let getByref r =
    Application(ItemGet (r, Value (String "get")), [])   

let setByref r v =
    Application(ItemGet (r, Value (String "set")), [v])   

let getAbstractSlot tparams (x: FSharpAbstractSignature) : Method =
    Method {
        MethodName = x.Name
        Parameters = x.AbstractArguments |> Seq.concat |> Seq.map (fun p -> getType tparams p.Type) |> List.ofSeq |> removeUnitParam
        ReturnType = getType tparams x.AbstractReturnType
        Generics   = x.MethodGenericParameters.Count
    } 

let getMember (x : FSharpMemberOrFunctionOrValue) : Member =
    let name = x.CompiledName

//    if name = "get_IsNone" then
//        let u = isUnionUsingNull x.EnclosingEntity
//        ()
    if name = ".cctor" then Member.StaticConstructor else

    let tparams = 
        Seq.append x.EnclosingEntity.GenericParameters x.GenericParameters
        |> Seq.distinctBy (fun p -> p.Name)
        |> Seq.mapi (fun i p -> p.Name, i) |> Map.ofSeq

    let isInstance = x.IsInstanceMember

    let compiledAsStatic =
        isInstance && (
            x.IsExtensionMember || (
                not (x.Attributes |> hasCompilationRepresentation CompilationRepresentationFlags.Instance) &&
                x.EnclosingEntity.Attributes |> hasCompilationRepresentation CompilationRepresentationFlags.UseNullAsTrueValue
            ) 
        )    

    let getPars() =
        let ps =  
            x.CurriedParameterGroups |> Seq.concat |> Seq.map (fun p -> getType tparams p.Type) |> List.ofSeq |> removeUnitParam  
        if compiledAsStatic then
            concreteType (getTypeDefinition x.LogicalEnclosingEntity, 
                List.init x.LogicalEnclosingEntity.GenericParameters.Count (fun i -> GenericType i)
            ) :: ps
        else ps

//    let getremap() =
//        let s = x.AbstractSlotSignature
//        let otparams = 
//            Seq.append s.ClassGenericParameters s.MethodGenericParameters
//            |> Seq.distinctBy (fun p -> p.Name)
//            |> Seq.mapi (fun i p -> p.Name, i) |> Map.ofSeq
//
//        x.
//
//        let m =
//            Seq.append tyPars mPars |> Seq.mapi (fun i t -> 
//                match getType otparams t with
//                | GenericType j -> Some (j, i)
//                | _ -> None
//            ) |> Seq.choose id |> Map.ofSeq
//        let fe (y: FSharpDelegateSignature) =
//            y.
//
//        let rec remap t =
//            match t with
//            | ConcreteType { Entity = td; Generics = ts } -> ConcreteType { Entity = td; Generics = List.map remap ts }
//            | GenericType i -> GenericType m.[i]
//            | ArrayType (t, r) -> ArrayType (remap t, r)
//            | TupleType ts -> TupleType (List.map remap ts)
//            | FSharpFuncType (a, r) -> FSharpFuncType (remap a, remap r)
//            | _ -> t
//        remap
////        ps |> List.map remap

    if name = ".ctor" then
        Member.Constructor <| Constructor {
            CtorParameters = getPars()
        }  
    else
//        let name =
//            match name.LastIndexOf '-' with
//            | -1 -> name
//            | i -> name.[i + 1 ..]
        if x.IsOverrideOrExplicitInterfaceImplementation then
            // TODO: multiple abstract slots implemented
            let s = x.ImplementedAbstractSignatures |> Seq.head

            let iTparams = 
                Seq.append s.DeclaringTypeGenericParameters s.MethodGenericParameters
                |> Seq.distinctBy (fun p -> p.Name)
                |> Seq.mapi (fun i p -> p.Name, i) |> Map.ofSeq
            
            let i = getTypeDefinition s.DeclaringType.TypeDefinition

            let meth = getAbstractSlot iTparams s

            if x.IsExplicitInterfaceImplementation then
                Member.Implementation(i, meth)    
            else
                Member.Override(i, meth)    

//            Some (getTypeDefinition iTyp.TypeDefinition), x.IsExplicitInterfaceImplementation, remap tyPars mPars                 
        else 
        
            Member.Method(
                isInstance && not compiledAsStatic,
                Method {
                    MethodName = name
                    Parameters = getPars()
                    ReturnType = getType tparams x.ReturnParameter.Type
        //                    try getType tparams x.ReturnParameter.Type
        //                    with _ -> 
        //                        // TODO: why
        //                        GenericType 0
                    Generics   = tparams.Count - x.EnclosingEntity.GenericParameters.Count
                } 
            )

//        match iTyp with
//        | None ->
//            Member.Method(isInstance && not compiledAsStatic, meth)
//        | Some iTyp ->
//            if isIntf then
//                Member.Implementation (iTyp, meth)
//            else
//                Member.Override (iTyp, meth) 

//let getMethod (x : FSharpMemberOrFunctionOrValue) =
//    let tparams = 
//        Seq.append x.EnclosingEntity.GenericParameters x.GenericParameters
//        |> Seq.mapi (fun i p -> p.Name, i) |> Map.ofSeq
//    Hashed {
//        MethodName = x.CompiledName
////        DefinedBy = getTypeDefinition x.EnclosingEntity
//        Parameters = x.CurriedParameterGroups |> Seq.concat |> Seq.map (fun p -> getType tparams p.Type) |> List.ofSeq
//        ReturnType = getType tparams x.ReturnParameter.Type
//        Generics = x.GenericParameters.Count
//    }

let getAndRegisterTypeDefinition (comp: M.Compilation) (td: FSharpEntity) =
    let res = getTypeDefinition td
 
//    let tparams = 
//        lazy
//        td.GenericParameters
//        |> Seq.mapi (fun i p -> p.Name, i) |> Map.ofSeq

//    if td.IsDelegate then 
//        if not (comp.HasCustomType res) then
//            let sign = 
//                try
//                    td.FSharpDelegateSignature
//                with e ->
//                    failwithf "%s: %s" e.Message res.Value.FullName
//            let info =
//                M.DelegateInfo {
//                    DelegateArgs =
//                        sign.DelegateArguments 
//                        |> Seq.map (fun (n, t) -> n, getType tparams.Value t) |> List.ofSeq
//                    ReturnType = getType tparams.Value sign.DelegateReturnType
//                }
//            comp.AddCustomType(res, info)
//        comp.ReflectCustomType res
    res

type Capturing(var) =
    inherit Transformer()

    let mutable captVal = None
    let mutable scope = 0

    override this.TransformId i =
        if scope > 0 && i = var then
            match captVal with
            | Some c -> c
            | _ ->
                let c = Id.New(?name = var.Name)
                captVal <- Some c
                c
        else i

    override this.TransformFunction (args, body) =
        scope <- scope + 1
        let res = base.TransformFunction (args, body)
        scope <- scope - 1
        res

    member this.CaptureValueIfNeeded expr =
        let res = this.TransformExpression expr  
        match captVal with
        | None -> res
        | Some c ->
            Application (Function ([c], Return res), [Var var])        

let getRange (range: Microsoft.FSharp.Compiler.Range.range) =
    {   
        FileName = range.FileName
        Start = range.StartLine, range.StartColumn + 1
        End = range.EndLine, range.EndColumn + 1
    }

let getSourcePos (x: FSharpExpr) =
    getRange x.Range

let withSourcePos (x: FSharpExpr) (expr: Expression) =
    ExprSourcePos (getSourcePos x, expr)

type FixCtorTransformer(?thisExpr) =
    inherit Transformer()

    let mutable firstOcc = true

    let thisExpr = defaultArg thisExpr This

    override this.TransformSequential (es) =
        match es with
        | h :: t -> Sequential (this.TransformExpression h :: t)
        | _ -> Undefined

    override this.TransformLet(a, b, c) =
        Let(a, b, this.TransformExpression c)

    override this.TransformConditional(a, b, c) =
        Conditional(a, this.TransformExpression b, this.TransformExpression c)   
        
    override this.TransformLetRec(a, b) =
        LetRec(a, this.TransformExpression b) 

    override this.TransformStatementExpr(a) = StatementExpr a

    override this.TransformCtor (t, c, a) =
        if not firstOcc then Ctor (t, c, a) else
        firstOcc <- false
        if t.Entity = sysObjDef then thisExpr
//            elif t.Entity.Value.FullName.Contains "FSharpRef" then
//                failwithf "fixCtor: %A" expr
        // TODO: correct exception chaining
        elif (let fn = t.Entity.Value.FullName in fn = "WebSharper.ExceptionProxy" || fn = "System.Exception") then 
//                let e = Id.New "$error"
//                errorId := Some e
//                NewVar(e, Ctor(t, c, a) |> withSourcePosOfExpr inst)
            match a with
            | [] -> Undefined
            | [msg] -> ItemSet(thisExpr, Value (String "message"), msg)
            | _ -> failwith "Too many arguments for Error"
        else
            BaseCtor(thisExpr, t, c, a) 

//    override this.TransformNewRecord (typ, fieldVals) =
//        if not firstOcc then NewRecord (typ, fields) else
//        firstOcc <- false
//        Sequential [
//            FieldSet(Thism )
//        ]

let fixCtor expr =
    FixCtorTransformer().TransformExpression(expr)

let rec transformExpression (env: Environment) (expr: FSharpExpr) =
    let inline tr x = transformExpression env x
    try
        match expr with
        | BasicPatterns.Value(var) ->
            if var.IsModuleValueOrMember then
                let td = getAndRegisterTypeDefinition env.Compilation var.EnclosingEntity
                match getMember var with
                | Member.Method (_, m) -> // TODO: instance methods represented as static
                    let ids = List.init var.CurriedParameterGroups.Count (fun _ -> Id.New "$x")  
                    // TODO : generics
                    // TODO : this is probably wrong, also have to detuple within parameter groups
                    let body = Call (None, concrete (td, []), concrete (m, []), ids |> List.map Var)
                    let res = List.foldBack (fun i b -> Lambda ([i], b)) ids body
                    if var.IsMutable then getByref res else res
                // TODO : constructor as a value
                | _ -> parsefailf "Module member is not a method"
    //            Call(None, concrete (td, []), "get_" + var.CompiledName, [])                      // TODO setter
    //            FieldGet (None, concrete (td, []), var.CompiledName) 
    //            ItemGet (Value (String "TODO: module access"), Value (String var.CompiledName))
//            elif var.IsMemberThisValue || var.IsConstructorThisValue then
//                This 
            else
                if isUnit var.FullType then
                    Undefined
                else
                    let v, k = env.LookupVar var
                    match k with
                    | LocalVar -> Var v  
                    | ByRefArg -> getByref (Var v)
                    | ThisArg -> This
        | BasicPatterns.Lambda(arg, body) ->
    
            let lArg, env =
                if isUnit arg.FullType then [], env
                else 
                    let v = Id.New(arg.DisplayName)
                    [ v ], env.WithVar(v, arg)
            let env = env.WithTParams(arg.GenericParameters |> Seq.map (fun p -> p.Name) |> List.ofSeq)
            Lambda(lArg, (body |> transformExpression env))
        | BasicPatterns.Application(func, types, args) ->
            let args =
                match types with
                | [t] when isUnit t -> []
                | _ -> args
            match ignoreExprSourcePos (tr func) with
            | CallNeedingMoreArgs(thisObj, td, m, ca) ->
                Call(thisObj, td, m, ca @ (args |> List.map tr))
            | trFunc ->
                Seq.fold (fun f a -> Application(f, [tr a])) trFunc args
        | BasicPatterns.Let((id, value), body) ->
            let i = Id.New(id.DisplayName)
            let trValue = tr value
            let env = env.WithVar(i, id, if isByRef id.FullType then ByRefArg else LocalVar)
            let inline tr x = transformExpression env x
            if id.IsMutable then
                Sequential [ NewVar(i, trValue); tr body ]
            else
                Let (i, trValue, tr body)
        | BasicPatterns.LetRec(defs, body) ->
            let mutable env = env
            let ids = defs |> List.map (fun (id, _) ->
                let i = Id.New(id.DisplayName)
                env <- env.WithVar(i, id, if isByRef id.FullType then ByRefArg else LocalVar)
                i
            )
            let inline tr x = transformExpression env x
            LetRec (
                Seq.zip ids defs 
                |> Seq.map (fun (i, (_, v)) -> i, tr v) |> List.ofSeq, 
                tr body
            )
        | BasicPatterns.Call(this, meth, typeGenerics, methodGenerics, arguments) ->
            let td = getAndRegisterTypeDefinition env.Compilation meth.EnclosingEntity
            //let tparams = meth.GenericParameters |> Seq.mapi (fun i p -> p.Name, i) |> Map.ofSeq        
            if td.Value.FullName = "Microsoft.FSharp.Core.Operators" && meth.CompiledName = "Reraise" then
                StatementExpr (Throw (Var env.Exception.Value))    
            else
                let t = concrete (td, typeGenerics |> List.map (getType env.TParams))
                let args = 
                    arguments |> List.map (fun a ->
                        let ta = tr a
                        if isByRef a.Type then
                            match ignoreExprSourcePos ta with
                            | Application(ItemGet (r, Value (String "get")), []) -> r
                            | _ -> ta
                        else ta
                    )
//                        List.map tr arguments
                let args =
                    match args with
                    | [ Undefined | Value Null ] -> []
                    | _ -> args
                match getMember meth with
                | Member.Method (isInstance, m) -> 
                    let mt = concrete (m, methodGenerics |> List.map (getType env.TParams))
                    if isInstance then
                        Call (Option.map tr this, t, mt, args)
                    else 
                        if meth.IsInstanceMember && not meth.IsExtensionMember then
                            CallNeedingMoreArgs (None, t, mt, Option.toList (Option.map tr this) @ args)
                        else 
                            Call (None, t, mt, Option.toList (Option.map tr this) @ args)
                | Member.Implementation (i, m) ->
                    let t = concrete (i, typeGenerics |> List.map (getType env.TParams))
                    let mt = concrete (m, methodGenerics |> List.map (getType env.TParams))
                    Call (Option.map tr this, t, mt, args)
    //                CallInterface (tr this.Value, t, mt, args)            
                | Member.Override (_, m) ->
                    let mt = concrete (m, methodGenerics |> List.map (getType env.TParams))
                    Call (Option.map tr this, t, mt, args)
                | Member.Constructor c -> Ctor (t, c, args)
                | Member.StaticConstructor -> parsefailf "Invalid: direct call to static constructor" //CCtor t 
        | BasicPatterns.Sequential _ ->
            let rec getSeq acc expr =
                match expr with            
                | BasicPatterns.Sequential (f, s) ->
                    getSeq (f :: acc) s   
                | _ -> expr :: acc
            getSeq [] expr |> List.rev |> List.map tr |> Sequential
        | BasicPatterns.Const (value, _) ->
            match value with
            | x when obj.ReferenceEquals(x, null) -> Null      
            | :? bool   as x -> Bool   x
            | :? byte   as x -> Byte   x
            | :? char   as x -> Char   x
            | :? double as x -> Double x
            | :? int    as x -> Int    x
            | :? int16  as x -> Int16  x
            | :? int64  as x -> Int64  x
            | :? sbyte  as x -> SByte  x
            | :? single as x -> Single x
            | :? string as x -> String x
            | :? uint16 as x -> UInt16 x
            | :? uint32 as x -> UInt32 x
            | :? uint64 as x -> UInt64 x
            | _ -> parsefailf "F# constant value not recognized: %A" value
            |> Value
        | BasicPatterns.IfThenElse (cond, then_, else_) ->
            Conditional(tr cond, tr then_, tr else_)    
        | BasicPatterns.NewObject (ctor, typeGenerics, arguments) -> 
            let td = getAndRegisterTypeDefinition env.Compilation ctor.EnclosingEntity
    //        let tparams = constructor_.GenericParameters |> Seq.mapi (fun i p -> p.Name, i) |> Map.ofSeq
            let t = concrete (td, typeGenerics |> List.map (getType env.TParams))
            let args = List.map tr arguments
            match getMember ctor with
            | Member.Constructor c -> Ctor (t, c, args)
            | _ -> parsefailf "Expected a constructor call"
        | BasicPatterns.TryFinally (body, final) ->
            let res = Id.New "$t"
            Sequential [
                StatementExpr (TryFinally(ExprStatement(NewVar(res, tr body)), ExprStatement (tr final)))
                Var res
            ]
        | BasicPatterns.TryWith (body, var, filter, e, catch) -> // TODO: var, filter?
            let err = Id.New e.DisplayName
            let res = Id.New "$t"
            Sequential [
                StatementExpr (
                    TryWith(ExprStatement(NewVar(res, tr body)), 
                        Some err, 
                        (ExprStatement (VarSet(res, transformExpression (env.WithException(err, e)) catch)))))
                Var res
            ]
        | BasicPatterns.NewArray (_, items) ->
            NewArray (items |> List.map tr)              
        | BasicPatterns.NewTuple (_, items) ->
            NewArray (items |> List.map tr)              
        | BasicPatterns.WhileLoop (cond, body) ->
            StatementExpr(While(tr cond, ExprStatement (tr body)))
        | BasicPatterns.ValueSet (var, value) ->
            if var.IsModuleValueOrMember then
                let td = getAndRegisterTypeDefinition env.Compilation var.EnclosingEntity
                match getMember var with
                | Member.Method (_, m) -> // TODO: instance methods represented as static
                    let ids = List.init var.CurriedParameterGroups.Count (fun _ -> Id.New())  
                    // TODO : generics
                    // TODO : this is probably wrong, also have to detuple within parameter groups
                    let body = Call (None, concrete (td, []), concrete (m, []), ids |> List.map Var)
                    setByref (List.foldBack (fun i b -> Lambda ([i], b)) ids body) (tr value)
                | _ -> parsefailf "Module member is not a method"
            else
                let v, k = env.LookupVar var
                match k with
                | LocalVar -> VarSet(v, tr value) 
                | ByRefArg -> setByref (Var v) (tr value)
                | ThisArg -> failwith "'this' parameter cannot be set"
        | BasicPatterns.TupleGet (_, i, tuple) ->
            ItemGet(tr tuple, Value (Int i))   
        | BasicPatterns.FastIntegerForLoop (start, end_, body, up) ->
            let j = Id.New "$j"
            let i, trBody =
                match ignoreExprSourcePos (tr body) with
                | Function ([i], Return b) -> i, b
                | _ -> parsefailf "Unexpected form of consumeExpr in FastIntegerForLoop pattern"     
            For (
                Some (Sequential [NewVar(i, tr start); NewVar (j, tr end_)]), 
                Some (if up then Binary(Var i, BinaryOperator.``<=``, Var j) else Binary(Var i, BinaryOperator.``>=``, Var j)), 
                Some (if up then MutatingUnary(MutatingUnaryOperator.``()++``, Var i)  else MutatingUnary(MutatingUnaryOperator.``()--``, Var i)), 
                ExprStatement (Capturing(i).CaptureValueIfNeeded(trBody))
            ) |> StatementExpr
        | BasicPatterns.TypeTest (typ, expr) ->
            TypeCheck (tr expr, getType env.TParams typ)
        | BasicPatterns.Coerce (typ, expr) ->
            tr expr // TODO: type check when possible
        | BasicPatterns.NewUnionCase (typ, case, exprs) ->
            let annot = attrReader.GetMemberAnnot(A.TypeAnnotation.Empty, case.Attributes) 
            match annot.Kind with
            | Some (A.MemberKind.Constant c) -> c
            | _ ->
            let i = typ.TypeDefinition.UnionCases |> Seq.findIndex (fun c -> c.CompiledName = case.CompiledName)
//            let t = 
//                match getType env.TParams typ with
//                | ConcreteType ct -> ct
//                | _ -> failwith "Expected a union type"
            CopyCtor(
                getAndRegisterTypeDefinition env.Compilation typ.TypeDefinition,
                Object (
                    ("$", Value (Int i)) ::
                    (exprs |> List.mapi (fun j e -> "$" + string j, tr e)) 
                )
            )
        | BasicPatterns.UnionCaseGet (expr, typ, case, field) ->
            let i = case.UnionCaseFields |> Seq.findIndex (fun f -> f = field)
            ItemGet(tr expr, Value (String ("$" + string i)))   
        | BasicPatterns.UnionCaseTest (expr, typ, case) ->
            let annot = attrReader.GetMemberAnnot(A.TypeAnnotation.Empty, case.Attributes) 
            match annot.Kind with
            | Some (A.MemberKind.Constant c) -> Binary (tr expr, BinaryOperator.``==``, c)
            | _ ->
            let i = typ.TypeDefinition.UnionCases |> Seq.findIndex (fun c -> c.CompiledName = case.CompiledName)
            Binary(ItemGet(tr expr, Value (String "$")), BinaryOperator.``==``, Value (Int i))
        | BasicPatterns.UnionCaseTag (expr, typ) ->
            ItemGet(tr expr, Value (String "$"))
        | BasicPatterns.NewRecord (typ, items) ->
            let t =
                match getType env.TParams typ with
                | ConcreteType ct -> ct
                | _ -> parsefailf "Expected a record type"
            NewRecord (t, List.map tr items)
        | BasicPatterns.DecisionTree (matchValue, cases) ->
            let i = Id.New "$i"
            let c = Id.New "$c"
            let r = Id.New "$r"
            let env = { env with MatchVars = Some (i, c) }
            Sequential [
                WithVars([i; c; r], transformExpression env matchValue)
                StatementExpr(
                    Switch(
                        Var i, 
                        cases |> List.mapi (fun j (ci, e) -> 
                            Some (Value (Int j)), 
                            Block [
                                let mutable env = env 
                                yield! ci |> Seq.mapi (fun captIndex cv ->
                                    let i = Id.New cv.DisplayName
                                    env <- env.WithVar (i, cv)
                                    (VarDeclaration(i, ItemGet(Var c, Value (Int captIndex))))
                                )
                                yield ExprStatement(VarSet(r, transformExpression env e)) 
                                yield Break None 
                            ]
                        )
                    )
                )
                Var r
            ]
        | BasicPatterns.DecisionTreeSuccess (index, results) ->
            let i, c = env.MatchVars.Value
            Sequential [
                yield VarSet (i, Value (Int index))
                match results |> List.map tr with
                | [] -> ()
                | matchCaptures -> yield VarSet (c, NewArray matchCaptures) 
            ]
        | BasicPatterns.ThisValue (typ) ->
            This
        | BasicPatterns.FSharpFieldGet (thisOpt, typ, field) ->
            let t = 
                match getType env.TParams typ with
                | ConcreteType ct -> ct
                | _ -> parsefailf "Expected a record type"
    //        let t = concrete (getTypeDefinition typ.TypeDefinition, typ.GenericArguments |> Seq.map (getType env.TParams) |> List.ofSeq)
            FieldGet(thisOpt |> Option.map tr, t, field.Name)
    //        match thisOpt with
    //        | Some this ->
    //            ItemGet(tr this, Value (String field.Name))  // TODO : field renames 
    //        | _ -> failwith "TODO"
        | BasicPatterns.FSharpFieldSet (thisOpt, typ, field, value) ->
            let t = 
                match getType env.TParams typ with
                | ConcreteType ct -> ct
                | _ -> parsefailf "Expected a record type"
    //        let t = concrete (getTypeDefinition typ.TypeDefinition, typ.GenericArguments |> Seq.map (getType env.TParams) |> List.ofSeq)
            FieldSet(thisOpt |> Option.map tr, t, field.Name, tr value)
    //        match thisOpt with
    //        | Some this ->
    //            ItemSet(tr this, Value (String field.Name), tr value)  // TODO : field renames 
    //        | _ -> failwith "TODO"
        | BasicPatterns.AddressOf expr ->
            let e = ignoreExprSourcePos (tr expr)
            match e with
            | Var v ->
                makeByref e (fun value -> VarSet(v, value))
            | ItemGet(o, i) ->
                makeByref e (fun value -> ItemSet(o, i, value))
            | FieldGet(o, t, f) ->
                makeByref e (fun value -> FieldSet(o, t, f, value))        
            | Application(ItemGet (r, Value (String "get")), []) ->
                makeByref e (fun value -> Application(ItemGet (r, Value (String "set")), [value]))        
            | _ -> failwithf "AddressOf error" // not on a Var or ItemGet: %+A" e 
        | BasicPatterns.AddressSet (addr, value) ->
            match addr with
            | BasicPatterns.Value(var) ->
                let v, _ = env.LookupVar var
                setByref (Var v) (tr value)
            | _ -> failwith "AddressSet not on a Value"
        | BasicPatterns.ObjectExpr (typ, expr, overrides, interfaces) ->
            let o = Id.New "$o"
            Sequential [
                yield NewVar(o, CopyCtor(getAndRegisterTypeDefinition env.Compilation typ.TypeDefinition, Object []))
                for ovr in Seq.append overrides (interfaces |> Seq.collect snd) do
                    let i = getAndRegisterTypeDefinition env.Compilation ovr.Signature.DeclaringType.TypeDefinition
                    let s = getAbstractSlot env.TParams ovr.Signature
                    let mutable env = env
                    let thisVar, vars =
                        match ovr.CurriedParameterGroups with
                        | [t] :: a ->
                            let thisVar = Id.New(t.DisplayName) //env.AddVar(Id.New(t.DisplayName), t)
                            env <- env.WithVar(thisVar, t)
                            thisVar,
                            a |> Seq.concat |> Seq.map (fun v ->
                                let vv = Id.New(v.DisplayName)
                                env <- env.WithVar(vv, v)
                                vv
                            ) |> List.ofSeq 
                        | _ ->
                            failwith "Wrong `this` argument in object expression override"
                    let b = FuncWithThis (thisVar, vars, Return (transformExpression env ovr.Body)) 
                    yield ItemSet(Var o, OverrideName(i, s), b)
                yield FixCtorTransformer(Var o).TransformExpression(tr expr)
                yield Var o
            ]
        | BasicPatterns.DefaultValue typ ->
            Value Null
        | BasicPatterns.NewDelegate (typ, arg) ->
            // TODO : loop for exact length of delegate type
            let rec loop acc = function
                | BasicPatterns.Lambda (var, body) -> loop (var :: acc) body
                | body -> (List.rev acc, body)

            match loop [] arg with
            | ([], BasicPatterns.Application (f, _, [BasicPatterns.Const (null, _)])) ->
                NewArray [ tr f ]
            | vars, body ->
                let mutable env = env
                let args = 
                    vars |> List.map (fun v -> 
                        let vv = Id.New(v.DisplayName) 
                        env <- env.WithVar(vv, v)
                        vv
                    )
                NewArray [ Lambda (args, transformExpression env body) ]
            | _ -> failwith "Failed to translate delegate creation"
        | BasicPatterns.TypeLambda (gen, expr) -> tr expr
        | BasicPatterns.Quote expr -> tr expr
        | BasicPatterns.BaseValue _ -> Base
        // I_ldelema (NormalAddress,false,ILArrayShape [(Some 0, null)],TypeVar 0us)](arr,0)
        | BasicPatterns.ILAsm("[I_ldelema (NormalAddress,false,ILArrayShape [(Some 0, null)],TypeVar 0us)]", _, [ arr; i ]) ->
            let arrId = Id.New "$a"
            let iId = Id.New "$i"
            Let (arrId, tr arr, Let(iId, tr i, makeByref (ItemGet(Var arrId, Var iId)) (fun value -> ItemSet(Var arrId, Var iId, value))))
        | BasicPatterns.ILAsm (s, _, _) ->
             parsefailf "Unrecognized ILAsm: %s" s
        | BasicPatterns.ILFieldGet          _ -> parsefailf "F# pattern not handled: ILFieldGet"
        | BasicPatterns.ILFieldSet          _ -> parsefailf "F# pattern not handled: ILFieldSet"
        | BasicPatterns.TraitCall(sourceTypes, traitName, typeArgs, typeInstantiation, argExprs) ->
            if sourceTypes.Length <> 1 then parsefailf "TODO: TraitCall with multiple source types" 
            if sourceTypes <> typeArgs then parsefailf "TODO: TraitCall different sourceTypes and typeArgs"
            if not typeInstantiation.IsEmpty then parsefailf "TODO: TraitCall with generic instantiation" 
            match argExprs with
            | t :: a -> 
                let meth =
                    Method {
                        MethodName = traitName
                        Parameters = a |> List.map (fun x -> getType env.TParams x.Type)
                        ReturnType = getType env.TParams expr.Type
                        Generics   = 0
                    } 
                Call(Some (tr t), concrete(getAndRegisterTypeDefinition env.Compilation t.Type.TypeDefinition, []), concrete(meth, []), a |> List.map tr)  
            | _ ->
                failwith "Impossible: TraitCall must have a this argument"
//            failwithf "TODO: TraitCall sourceTypes: %A traitName: %s typeArgs: %A typeInstantiation %A: argExprs: %A"
//                sourceTypes traitName typeArgs typeInstantiation argExprs
        | BasicPatterns.UnionCaseSet _ ->
            parsefailf "UnionCaseSet pattern is only allowed in FSharp.Core"
        | _ -> parsefailf "F# expression not recognized"
    with e ->
        let msg =
            match e with
            | ParseError m -> m
            | _ -> "Error while reading F# code: " + e.Message + " " + e.StackTrace
        env.Compilation.AddError(Some (getSourcePos expr), Metadata.SourceError msg)
        WebSharper.Compiler.ToJavaScript.errorPlaceholder        
    |> withSourcePos expr