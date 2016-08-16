﻿namespace Freya.Routers.Uri.Template

#nowarn "46"

open Aether
open Aether.Operators
open FParsec
open Freya.Core
open Freya.Core.Operators
open Freya.Optics.Http
open Freya.Polyfills
open Freya.Routers
open Freya.Types.Http
open Freya.Types.Uri.Template

(* Types

   The core types used in routing evaluation, namely for traversal state of the
   compiled graph and the return of a clear result to evaluation function. *)

(* Evaluation

   The result of routing evaluation, including matched data and the associated
   pipeline to run in the case where a route was matched. *)

type internal Evaluation =
    | Matched of UriTemplateData * Pipeline
    | Unmatched

(* Traversal

   State and carried data as part of the traversal of a compiled routing graph,
   including optics to provide lens based modification of the traversal while
   in progress. *)

type internal Traversal =
    | Traversal of Method * Position * UriTemplateData

 and internal Position =
    | Position of string * int

(* Request

   Optics and functions for working with the request to get a raw form path and
   query value. *)

[<RequireQualifiedAccess>]
module internal Request =

    let private queryString_ =
            State.value_<string> "owin.RequestQueryString"
        >-> Option.unsafe_

    let private merge =
        function | path, query when query <> "" -> sprintf "%s?%s" path query
                 | path, _ -> path

    let method =
            !. Request.method_

    let pathAndQuery =
            fun pathRaw path query ->
                match pathRaw, path, query with
                | Some pathRaw, _, query -> merge (pathRaw, query)
                | _, path, query -> merge (path, query)
        <!> !. Request.pathRaw_
        <*> !. Request.path_
        <*> !. queryString_

(* Evaluation

   Evaluation of a compiled structure, finding the matching routes (where
   present) and returning the route with the highest precedence, determined by
   the order that the route was specified in the original list of routes.

   The search is exhaustive and will find all matches, as it's needed to ensure
   that the order of the declarations is the deciding factor, not the sorted
   structure topology. *)

[<AutoOpen>]
[<CompilationRepresentation (CompilationRepresentationFlags.ModuleSuffix)>]
module internal Evaluation =

    (* Traversal

       Traversal of the recursive data structure representing the routing graph
       which effectively forms a trie. The structure is traversed exhaustively,
       finding all matching endpoints, at which point the endpoint with the
       highest precedence is selected and returned (if a matching endpoint is
       found). *)

    let rec private traverse route traversal =
        List.concat [
            inclusion route traversal
            progression route traversal ]

    (* Inclusion

       Inclusion consists of the case where the path and query have been
       exhausted and candidate endpoints may be included in the set of possible
       endpoints. The endpoints are filtered using an active pattern based on
       method match and returned along with the associated data state and the
       precedence. *)

    and private inclusion route =
        function | Traversal (method, Complete _, data) -> include method data route
                 | _ -> []

    and private (|Complete|_|) =
        function | Position (content, caret) when caret >= content.Length -> Some (content, caret)
                 | _ -> None

    and private include method data =
        function | Route (Endpoints method endpoints, _) -> List.map (fun (Endpoint (precedence, _, pipe)) -> precedence, data, pipe) endpoints
                 | _ -> []

    and private (|Endpoints|_|) method =
            List.filter (filterEndpoints method)
         >> mapEndpoints

    and private filterEndpoints method =
        function | Endpoint (_, All, _) -> true
                 | Endpoint (_, Methods methods, _) when List.contains method methods -> true
                 | _ -> false

    and private mapEndpoints =
        function | [] -> None
                 | endpoints -> Some endpoints

    (* Progression

       Progression consists of the case where the path and query is not
       exhausted and so the remaining matching branches are also returned given
       the current subset of remainders which match the current path and query.

       The remainder matching and parser matching per remainder is defined as a
       simple set of filtering active patterns. *)

    and private progression route =
        function | Traversal (method, position, data) -> progress method position data route

    and private progress method position data =
        function | Route (_, Remainders remainders) -> (List.map (remainder method position data) >> List.concat) remainders
                 | _ -> []

    and private (|Remainders|_|) =
        function | [] -> None
                 | remainders -> Some remainders

    and private remainder method position data =
        function | Remainder (Match position (data', position'), route) -> traverse route (Traversal (method, position', data + data'))
                 | _ -> []

    and private (|Match|_|) position =
        function | parser -> runCaret parser position

    and private runCaret parser =
        function | Position (content, caret) -> result content caret (run parser content caret)

    and private run parser content =
        function | caret -> runParserOnSubstring parser () "" content caret (content.Length - caret)

    and private result content caret =
        function | Success (data, _, position) -> Some (data, Position (content, caret + (int position.Index)))
                 | _ -> None

    (* Selection

       Select the highest precedence data and pipeline pair from the given set
       of candidates, using the supplied precedence value. *)

    let rec private select =
        function | [] -> Unmatched
                 | endpoints -> Matched ((List.minBy orderEndpoint >> mapEndpoint) endpoints)

    and private orderEndpoint =
        function | (precedence, _, _) -> precedence

    and private mapEndpoint =
        function | (_, data, pipe) -> data, pipe

    (* Search

       Combine a list of all possible route matches and associated captured
       data, produced by a traversal of the compiled routing graph, with a
       selection of the matched route (data and pipeline pair) with the
       highest precedence, as measured by the order in which the routes were
       declared in the compilation phase. *)

    let rec private search route =
            traverse route >> select
        <!> initial

    and private initial =
            fun method pathAndQuery ->
                Traversal (method, Position (pathAndQuery, 0), UriTemplateData Map.empty)
        <!> Request.method
        <*> Request.pathAndQuery

    (* Evaluation

       Run a search on the routing graph. In the case of a match, write any
       captured data to the state to be interrogated later through the routing
       lenses, and return the value of executing the matched pipeline.

       In the case of a non-match, fall through to whatever follows the router
       instance. *)

    let evaluate route =
            function | Matched (data, pipe) -> Some (data, pipe)
                     | Unmatched -> None
        <!> search route