// $begin{copyright}
// 
// This file is part of WebSharper
// 
// Copyright (c) 2008-2011 IntelliFactory
// 
// GNU Affero General Public License Usage
// WebSharper is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License, version 3, as published
// by the Free Software Foundation.
//
// WebSharper is distributed in the hope that it will be useful, but WITHOUT
// ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or
// FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License
// for more details at <http://www.gnu.org/licenses/>.
//
// If you are unsure which license is appropriate for your use, please contact
// IntelliFactory at http://intellifactory.com/contact.
//
// $end{copyright}

[<IntelliFactory.WebSharper.Core.Attributes.Name "Queue">]
module private IntelliFactory.WebSharper.QueueProxy

[<Inline "$arr.splice($offset,$len)">]
let splice (arr: obj) (offset: int) (len: int) = X<unit>

[<JavaScript>]
let Clear (a: obj) =
    splice a 0 (a :?> obj []).Length

[<JavaScript>]
let Contains (a: obj) (el: 'T) =
    Seq.exists ((=) el) (a :?> seq<'T>)

[<JavaScript>]
let CopyTo (a: obj) (array: 'T[]) (index: int) =
    Array.blit (a :?> 'T []) 0 array index (a :?> 'T[]).Length

[<Proxy(typeof<System.Collections.Generic.Queue<_>>)>]
type private QueueProxy<'T when 'T : equality>

    [<Inline "$data">] (data: 'T []) =

    [<Inline "[]">]
    new () = QueueProxy [||]

    member this.Count with [<Inline "$this.length">] get () = X<int>

    [<Inline>]
    [<JavaScript>]
    member this.Clear() = Clear this

    [<Inline>]
    [<JavaScript>]
    member this.Contains(x: 'T) = Contains this x

    [<Inline>]
    [<JavaScript>]
    member this.CopyTo(array: 'T [], index: int) = CopyTo this array index

    [<Inline "$this[0]">]
    member this.Peek() = X<'T>

    [<Inline "$this.shift()">]
    member this.Dequeue() = X<'T>

    [<Inline "$this.push($x)">]
    member this.Enqueue(x: 'T) = X<unit>

    [<Inline "$this.slice(0)">]
    member this.ToArray() = data
