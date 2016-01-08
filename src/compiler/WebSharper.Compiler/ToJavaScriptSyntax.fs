﻿module WebSharper.Compiler.ToJavaScriptSyntax

module J = WebSharper.Core.JavaScript.Syntax
module I = WebSharper.Core.JavaScript.Identifier

open WebSharper.Core
open WebSharper.Core.AST

type P = WebSharper.Core.JavaScript.Preferences

type Environment =
    {
//        CaughtException : option<Expr<unit, unit>> // TODO
        AssemblyName : string
        Preference : WebSharper.Core.JavaScript.Preferences
        mutable ScopeVars : Set<string>
        mutable CompactVars : int
        mutable ScopeIds : Map<Id, string>
    }
    static member New(name, pref) =
        {
            AssemblyName = name
            Preference = pref    
//            CaughtException = None
            ScopeVars = Set.empty
            CompactVars = 0 
            ScopeIds = Map.empty
        }

    member this.Clone() =
        {
            AssemblyName = this.AssemblyName
            Preference = this.Preference    
//            CaughtException = this.CaughtException
            ScopeVars = this.ScopeVars
            CompactVars = this.CompactVars
            ScopeIds = this.ScopeIds
        }

let undef = J.Var "undefined"
let glob = J.Var "Global"

let transformId (env: Environment) (id: Id) =
    try Map.find id env.ScopeIds
    with _ -> 
        match id.Name with
        | Some n -> failwithf "Undefined variable found: %s" n
//        | Some n -> failwithf "Undefined variable found: %s, Scope vars: %s" n (env.ScopeVars |> String.concat " ")
        | _ -> failwith "Undefined variable found"
//        "MISSINGVAR" + I.MakeValid (id.Name |> Option.fill "_")

let formatter = WebSharper.Core.JavaScript.Identifier.MakeFormatter()

let defineId (env: Environment) (id: Id) =
    if env.Preference = P.Compact then
        let name = formatter env.CompactVars    
        env.CompactVars <- env.CompactVars + 1   
        env.ScopeIds <- env.ScopeIds |> Map.add id name
        name 
    else 
        let vars = env.ScopeVars
        let mutable name = (I.MakeValid (id.Name |> Option.fill "x"))
        while vars |> Set.contains name do
            name <- Resolve.newName name 
        env.ScopeVars <- vars |> Set.add name
        env.ScopeIds <- env.ScopeIds |> Map.add id name
        name
       
let invalidForm c =
    failwithf "invalid form at writing JavaScript: %s" c
    
let rec transformExpr (env: Environment) (expr: Expression) : J.Expression =
    let inline trE x = transformExpr env x
    let inline trI x = transformId env x
    match expr with
    | Undefined -> undef
    | This -> J.This
//        | Global -> glob
    | Var id -> J.Var (trI id)
    | Value v ->
        match v with
        | Null     -> J.Literal.Null
        | Bool   v -> if v then J.Literal.True else J.Literal.False
        | Byte   v -> J.Number (string v)
        | Char   v -> J.Number (string (int v))
        | Double v -> J.Number (string v)
        | Int    v -> J.Number (string v)
        | Int16  v -> J.Number (string v)
        | Int64  v -> J.Number (string v)
        | SByte  v -> J.Number (string v)
        | Single v -> J.Number (string v)
        | String v -> J.String v
        | UInt16 v -> J.Number (string v)
        | UInt32 v -> J.Number (string v)
        | UInt64 v -> J.Number (string v)
        |> J.Constant
    | Application (e, ps) -> J.Application (trE e, ps |> List.map trE)
    | VarSet (id, e) -> J.Binary(J.Var (trI id), J.BinaryOperator.``=``, trE e )  
    | ExprSourcePos (pos, e) -> 
        let jpos =
            {
                Assembly = env.AssemblyName
                File = pos.FileName
                Line = fst pos.Start
                Column = snd pos.Start
                EndLine = fst pos.End
                EndColumn = snd pos.End
            } : J.SourcePos
        J.ExprPos (trE e, jpos)
    | Function (ids, b) ->
        let innerEnv = env.Clone()
        let args = ids |> List.map (defineId innerEnv) 
        let body =
            match b |> transformStatement innerEnv with
            | J.Block b -> b |> List.map J.Action
            | b -> [ b |> J.Action ]
        J.Lambda(None, args, body)
    | ItemGet (x, y) -> (trE x).[trE y]
    | Binary (x, y, z) ->
        match y with
        | BinaryOperator.``!==``        -> J.Binary(trE x, J.BinaryOperator.``!==``       , trE z)
        | BinaryOperator.``!=``         -> J.Binary(trE x, J.BinaryOperator.``!=``        , trE z)
        | BinaryOperator.``%``          -> J.Binary(trE x, J.BinaryOperator.``%``         , trE z)
        | BinaryOperator.``&&``         -> J.Binary(trE x, J.BinaryOperator.``&&``        , trE z)
        | BinaryOperator.``&``          -> J.Binary(trE x, J.BinaryOperator.``&``         , trE z)
        | BinaryOperator.``*``          -> J.Binary(trE x, J.BinaryOperator.``*``         , trE z)
        | BinaryOperator.``+``          -> J.Binary(trE x, J.BinaryOperator.``+``         , trE z)
        | BinaryOperator.``,``          -> J.Binary(trE x, J.BinaryOperator.``,``         , trE z)
        | BinaryOperator.``-``          -> J.Binary(trE x, J.BinaryOperator.``-``         , trE z)
//        | BinaryOperator.``.``          -> J.Binary(trE x, J.BinaryOperator.``.``         , trE z)
        | BinaryOperator.``/``          -> J.Binary(trE x, J.BinaryOperator.``/``         , trE z)
        | BinaryOperator.``<<``         -> J.Binary(trE x, J.BinaryOperator.``<<``        , trE z)
        | BinaryOperator.``<=``         -> J.Binary(trE x, J.BinaryOperator.``<=``        , trE z)
        | BinaryOperator.``<``          -> J.Binary(trE x, J.BinaryOperator.``<``         , trE z)
        | BinaryOperator.``===``        -> J.Binary(trE x, J.BinaryOperator.``===``       , trE z)
        | BinaryOperator.``==``         -> J.Binary(trE x, J.BinaryOperator.``==``        , trE z)
        | BinaryOperator.``=``          -> J.Binary(trE x, J.BinaryOperator.``=``         , trE z)
        | BinaryOperator.``>=``         -> J.Binary(trE x, J.BinaryOperator.``>=``        , trE z)
        | BinaryOperator.``>>>``        -> J.Binary(trE x, J.BinaryOperator.``>>>``       , trE z)
        | BinaryOperator.``>>``         -> J.Binary(trE x, J.BinaryOperator.``>>``        , trE z)
        | BinaryOperator.``>``          -> J.Binary(trE x, J.BinaryOperator.``>``         , trE z)
        | BinaryOperator.``^``          -> J.Binary(trE x, J.BinaryOperator.``^``         , trE z)
        | BinaryOperator.``in``         -> J.Binary(trE x, J.BinaryOperator.``in``        , trE z)
        | BinaryOperator.``instanceof`` -> J.Binary(trE x, J.BinaryOperator.``instanceof``, trE z)
        | BinaryOperator.``|``          -> J.Binary(trE x, J.BinaryOperator.``|``         , trE z)
        | BinaryOperator.``||``         -> J.Binary(trE x, J.BinaryOperator.``||``        , trE z)
        | _ -> failwith "invalid BinaryOperator enum value"
    | ItemSet(x, y, z) -> (trE x).[trE y] ^= trE z
    | MutatingBinary(x, y, z) ->
        match y with
        | MutatingBinaryOperator.``=``    -> J.Binary(trE x, J.BinaryOperator.``=``    , trE z)
        | MutatingBinaryOperator.``+=``   -> J.Binary(trE x, J.BinaryOperator.``+=``   , trE z)
        | MutatingBinaryOperator.``-=``   -> J.Binary(trE x, J.BinaryOperator.``-=``   , trE z)
        | MutatingBinaryOperator.``*=``   -> J.Binary(trE x, J.BinaryOperator.``*=``   , trE z)
        | MutatingBinaryOperator.``/=``   -> J.Binary(trE x, J.BinaryOperator.``/=``   , trE z)
        | MutatingBinaryOperator.``%=``   -> J.Binary(trE x, J.BinaryOperator.``%=``   , trE z)
        | MutatingBinaryOperator.``&=``   -> J.Binary(trE x, J.BinaryOperator.``&=``   , trE z)
        | MutatingBinaryOperator.``^=``   -> J.Binary(trE x, J.BinaryOperator.``^=``   , trE z)
        | MutatingBinaryOperator.``|=``   -> J.Binary(trE x, J.BinaryOperator.``|=``   , trE z)
        | MutatingBinaryOperator.``<<=``  -> J.Binary(trE x, J.BinaryOperator.``<<=``  , trE z)
        | MutatingBinaryOperator.``>>=``  -> J.Binary(trE x, J.BinaryOperator.``>>=``  , trE z)
        | MutatingBinaryOperator.``>>>=`` -> J.Binary(trE x, J.BinaryOperator.``>>>=`` , trE z)
    | Object fs -> J.NewObject (fs |> List.map (fun (k, v) -> k, trE v))
    | New (x, y) -> J.New(trE x, y |> List.map trE)
    | Sequential x ->
        x |> List.map trE |> List.reduce (fun a b -> J.Binary(a, J.BinaryOperator.``,``, b))
    | Conditional (cond, then_, else_) ->
        J.Conditional(trE cond, trE then_, trE else_)
    | NewArray arr -> J.NewArray (List.map (trE >> Some) arr)
    | Unary(x, y) ->
        match x with
        | UnaryOperator.``!``    -> J.Unary(J.UnaryOperator.``!``, trE y)
        | UnaryOperator.``void`` -> J.Unary(J.UnaryOperator.``void``, trE y)
        | UnaryOperator.``+``    -> J.Unary(J.UnaryOperator.``+``, trE y)
        | UnaryOperator.``-``    -> J.Unary(J.UnaryOperator.``-``, trE y)
        | UnaryOperator.``~``    -> J.Unary(J.UnaryOperator.``~``, trE y)
        | UnaryOperator.typeof   -> J.Unary(J.UnaryOperator.typeof, trE y)
        | _ -> failwith "invalid UnaryOperator enum value"
    | MutatingUnary(x, y) ->
        match x with
        | MutatingUnaryOperator.``()++`` -> J.Postfix(trE y, J.PostfixOperator.``++``)
        | MutatingUnaryOperator.``()--`` -> J.Postfix(trE y, J.PostfixOperator.``--``)
        | MutatingUnaryOperator.``++()`` -> J.Unary(J.UnaryOperator.``++``, trE y)
        | MutatingUnaryOperator.``--()`` -> J.Unary(J.UnaryOperator.``--``, trE y)
        | MutatingUnaryOperator.delete   -> J.Unary(J.UnaryOperator.delete, trE y)
    | _ -> 
        invalidForm (fst (FSharp.Reflection.FSharpValue.GetUnionFields(expr, typeof<Expression>))).Name 

and transformStatement (env: Environment) (statement: Statement) : J.Statement =
    let inline trE x = transformExpr env x
    let inline trS x = transformStatement env x
    let flatten s =
        let res = ResizeArray()
        let mutable emptyDecls = ResizeArray()
        let mutable decls = ResizeArray() 
        let flushVars() =
            if emptyDecls.Count > 0 || decls.Count > 0 then
                if env.Preference = P.Compact then
                    Seq.append
                        (emptyDecls |> Seq.map (fun i -> i, None))
                        (decls |> Seq.map (fun (i, e) -> i, Some (trE e))) 
                    |> List.ofSeq |> J.Vars |> res.Add
                else 
                    res.Add (J.Vars (emptyDecls |> Seq.map (fun i -> i, None) |> List.ofSeq))
                    for i, e in decls do
                        res.Add (J.Vars [ i, Some (trE e) ])
                emptyDecls.Clear()
                decls.Clear()
        
        let rec add a =
            match ignoreStatementSourcePos a with 
            | Block b
            | Statements b -> b |> List.iter add
            | VarDeclaration (id, e) ->
                match ignoreExprSourcePos e with
                | Undefined ->   
                    emptyDecls.Add (defineId env id)
                | _ ->
                    decls.Add (defineId env id, e)
            | _ -> 
                flushVars()
                res.Add(trS a)
        s |> List.iter add
        flushVars()
        List.ofSeq res    
    let flattenS s =
        match ignoreStatementSourcePos s with
        | Block s 
        | Statements s -> flatten s
        | _ -> [ trS s ]
    match statement with
    | Empty -> J.Empty
    | Break(a) -> J.Break (a |> Option.map (fun l -> l.Name.Value))
    | Continue(a) -> J.Continue (a |> Option.map (fun l -> l.Name.Value))
    | ExprStatement e -> J.Ignore(trE e)
    | Block s -> J.Block (flatten s)
    | StatementSourcePos (_, s) -> trS s // TODO  
    | If(a, b, c) -> J.If(trE a, trS b, trS c)
    | Return a -> J.Return (match a with Undefined -> None | _ -> Some (trE a))
    | VarDeclaration (id, e) ->
        J.Vars ([defineId env id, match e with Undefined -> None | _ -> Some (trE e)])
    | While(a, b) -> J.While (trE a, trS b)
    | DoWhile(a, b) -> J.Do (trS a, trE b)
    | For(a, b, c, d) -> J.For(Option.map trE a, Option.map trE b, Option.map trE c, trS d)
    | Switch(a, b) -> 
        J.Switch(trE a, 
            b |> List.map (fun (l, s) -> 
                match l with 
                | Some l -> J.SwitchElement.Case (trE l, flattenS s) 
                | _ -> J.SwitchElement.Default (flattenS s)
            )
        )
    | Throw(a) -> J.Throw (trE a)
    | Labeled(a, b) -> J.Labelled(a.Name.Value, trS b)
    | TryWith(a, b, c) -> 
        J.TryWith(trS a, defineId env (b |> Option.fallback Id.New), trS c, None)
    | TryFinally(a, b) ->
        J.TryFinally(trS a, trS b)
    | ForIn(a, b, c) -> J.ForVarIn(defineId env a, None, trE b, trS c)
    | Statements a -> J.Block (a |> List.map trS) // TODO
    | _ -> 
        invalidForm (fst (FSharp.Reflection.FSharpValue.GetUnionFields(statement, typeof<Statement>))).Name 