﻿// Nu Game Engine.
// Copyright (C) Bryan Edds, 2013-2020.

namespace Nu
open System
open System.Collections.Generic
open System.Reflection
open SDL2
open Prime
open Nu

// HACK: I had to remove the [<RequireQualifiedAccess>] attribute from here because it was being interpreted in an
// ambiguous way by F# Interactive.
module Nu =

    let mutable private Initialized = false
    let private LoadedAssemblies = Dictionary<string, Assembly> HashIdentity.Structural

    let private tryPropagateByLens (left : World Lens) (right : World Lens) world =
        if right.Validate world then
            let value = right.GetWithoutValidation world
            (Option.get left.SetOpt) value world
        else world

    let private tryPropagateByName simulant leftName (right : World Lens) world =
        if right.Validate world then
            let value = right.GetWithoutValidation world
            match World.tryGetProperty leftName simulant world with
            | Some property ->
                if property.PropertyValue =/= value then
                    let property = { property with PropertyValue = value }
                    World.trySetProperty leftName property simulant world |> snd
                else world
            | None -> world
        else world

    let private tryPropagate simulant (left : World Lens) (right : World Lens) world =
        if notNull (left.This :> obj)
        then tryPropagateByLens left right world
        else tryPropagateByName simulant left.Name right world

    let internal unbind propertyBindingId propertyAddress world =

        // add binding to map
        let world =
            match world.PropertyBindingsMap.TryGetValue propertyAddress with
            | (true, propertyBindings) ->
                let propertyBindings = UMap.remove propertyBindingId propertyBindings
                if UMap.notEmpty propertyBindings then
                    let propertyBindingsMap = UMap.add propertyAddress propertyBindings world.PropertyBindingsMap
                    World.choose { world with PropertyBindingsMap = propertyBindingsMap }
                else
                    let propertyBindingsMap = UMap.remove propertyAddress world.PropertyBindingsMap
                    World.choose { world with PropertyBindingsMap = propertyBindingsMap }
            | (false, _) -> world

        // decrease bind counter
        let world =
            match propertyAddress.PASimulant with
            | :? Entity as entity ->
                match World.tryGetKeyedValue<UMap<Entity Address, int>> EntityBindingCountsId world with
                | Some entityBindingCounts ->
                    match UMap.tryFind entity.EntityAddress entityBindingCounts with
                    | Some entityBindingCount ->
                        let entityBindingCount = dec entityBindingCount
                        let entityBindingCounts =
                            if entityBindingCount = 0
                            then UMap.remove entity.EntityAddress entityBindingCounts
                            else UMap.add entity.EntityAddress entityBindingCount entityBindingCounts
                        let world = if entityBindingCount = 0 && entity.Exists world then World.setEntityPublishChangeBindings false entity world else world
                        World.addKeyedValue EntityBindingCountsId entityBindingCounts world
                    | None -> failwithumf ()
                | None -> failwithumf ()
            | _ -> world

        // fin
        world

    /// Initialize the Nu game engine.
    let init nuConfig =

        // init only if needed
        if not Initialized then

            // make types load reflectively from pathed (non-static) assemblies
            AppDomain.CurrentDomain.AssemblyLoad.Add
                (fun args -> LoadedAssemblies.[args.LoadedAssembly.FullName] <- args.LoadedAssembly)
            AppDomain.CurrentDomain.add_AssemblyResolve (ResolveEventHandler
                (fun _ args -> snd (LoadedAssemblies.TryGetValue args.Name)))

            // ensure the current culture is invariate
            Threading.Thread.CurrentThread.CurrentCulture <- Globalization.CultureInfo.InvariantCulture

            // init logging
            Log.init (Some "Log.txt")

            // init math module
            Math.init ()

            // init simulant modules
            WorldModuleGame.init ()
            WorldModuleScreen.init ()
            WorldModuleLayer.init ()
            WorldModuleEntity.init ()

            // init getPropertyOpt F# reach-around
            WorldTypes.getPropertyOpt <- fun propertyName propertied world ->
                match propertied with
                | :? Simulant as simulant ->
                    match World.tryGetProperty propertyName simulant (world :?> World) with
                    | Some property -> Some property.PropertyValue
                    | None -> None
                | _ -> None

            // init setPropertyOpt F# reach-around
            WorldTypes.setPropertyOpt <- fun propertyName propertied valueOpt ty world ->
                let world = world :?> World
                match propertied with
                | :? Simulant as simulant ->
                    match simulant with
                    | :? Entity as entity ->
                        match (valueOpt, WorldModuleEntity.EntitySetters.TryGetValue propertyName) with
                        | (Some value, (true, setter)) ->
                            let layer = entity.Parent
                            let screen = layer.Parent
                            let world = if not (World.getScreenExists screen world) then World.createScreen None world |> snd else world
                            let world = if not (World.getLayerExists layer world) then World.createLayer None screen world |> snd else world
                            let world = if not (World.getEntityExists entity world) then World.createEntity None DefaultOverlay layer world |> snd else world
                            let property = { PropertyValue = value; PropertyType = ty }
                            setter property entity world |> snd |> box
                        | (Some value, (false, _)) ->
                            let layer = entity.Parent
                            let screen = layer.Parent
                            let world = if not (World.getScreenExists screen world) then World.createScreen None world |> snd else world
                            let world = if not (World.getLayerExists layer world) then World.createLayer None screen world |> snd else world
                            let world = if not (World.getEntityExists entity world) then World.createEntity None DefaultOverlay layer world |> snd else world
                            let property = { PropertyValue = value; PropertyType = ty }
                            World.attachEntityProperty propertyName property entity world |> box
                        | (None, (true, _)) ->
                            World.destroyEntity entity world |> box
                        | (None, (false, _)) ->
                            World.detachEntityProperty propertyName entity world |> box
                    | :? Layer as layer ->
                        match (valueOpt, WorldModuleLayer.LayerSetters.TryGetValue propertyName) with
                        | (Some value, (true, setter)) ->
                            let screen = layer.Parent
                            let world = if not (World.getScreenExists screen world) then World.createScreen None world |> snd else world
                            let world = if not (World.getLayerExists layer world) then World.createLayer None screen world |> snd else world
                            let property = { PropertyValue = value; PropertyType = ty }
                            setter property layer world |> snd |> box
                        | (Some value, (false, _)) ->
                            let screen = layer.Parent
                            let world = if not (World.getScreenExists screen world) then World.createScreen None world |> snd else world
                            let world = if not (World.getLayerExists layer world) then World.createLayer None screen world |> snd else world
                            let property = { PropertyValue = value; PropertyType = ty }
                            World.attachLayerProperty propertyName property layer world |> box
                        | (None, (true, _)) ->
                            World.destroyLayer layer world |> box
                        | (None, (false, _)) ->
                            World.detachLayerProperty propertyName layer world |> box
                    | :? Screen as screen ->
                        match (valueOpt, WorldModuleScreen.ScreenSetters.TryGetValue propertyName) with
                        | (Some value, (true, setter)) ->
                            let world = if not (World.getScreenExists screen world) then World.createScreen None world |> snd else world
                            let property = { PropertyValue = value; PropertyType = ty }
                            setter property screen world |> snd |> box
                        | (Some value, (false, _)) ->
                            let world = if not (World.getScreenExists screen world) then World.createScreen None world |> snd else world
                            let property = { PropertyValue = value; PropertyType = ty }
                            World.attachScreenProperty propertyName property screen world |> box
                        | (None, (true, _)) ->
                            World.destroyScreen screen world |> box
                        | (None, (false, _)) ->
                            World.detachScreenProperty propertyName screen world |> box
                    | :? Game ->
                        match (valueOpt, WorldModuleGame.GameSetters.TryGetValue propertyName) with
                        | (Some value, (true, setter)) ->
                            let property = { PropertyValue = value; PropertyType = ty }
                            setter property world |> snd |> box
                        | (Some value, (false, _)) ->
                            let property = { PropertyValue = value; PropertyType = ty }
                            World.attachGameProperty propertyName property world |> box
                        | (None, (true, _)) ->
                            World.exit world |> box
                        | (None, (false, _)) ->
                            World.detachGameProperty propertyName world |> box
                    | _ -> failwithumf ()
                | _ -> box world

            // init handlePropertyChange F# reach-around
            WorldTypes.handlePropertyChange <- fun propertyName propertied handler world ->
                let simulant = propertied :?> Simulant
                let handler = handler :?> World PropertyChangeHandler
                let (unsubscribe, world) =
                    World.subscribeWith Gen.id
                        (fun (event : Event<obj, _>) world ->
                            let data = event.Data :?> ChangeData
                            let world = handler data.Value world
                            (Cascade, world))
                        (rtoa (Array.append [|"Change"; propertyName; "Event"|] (Address.getNames simulant.SimulantAddress)))
                        (Simulants.Game :> Simulant)
                        (world :?> World)
                (box unsubscribe, box world)

            // init handleUserDefinedCallback F# reach-around
            WorldTypes.handleUserDefinedCallback <- fun userDefined _ worldObj ->
                let world = worldObj :?> World
                let (simulant, left, right) = userDefined :?> Simulant * World Lens * World Lens
                let world =
                    if notNull (left.This :> obj)
                    then tryPropagateByLens left right world
                    else tryPropagateByName simulant left.Name right world
                (Cascade, world :> obj)

            WorldTypes.handleSubscribeAndUnsubscribeEventHook <- fun subscribing eventAddress _ worldObj ->
                // here we need to update the event publish flags for entities based on whether there are subscriptions to
                // these events. These flags exists solely for efficiency reasons. We also look for subscription patterns
                // that these optimization do not support, and warn the developer if they are invoked. Additionally, we
                // warn if the user attempts to subscribe to a Change event with a wildcard as doing so is not supported.
                let world = worldObj :?> World
                let eventNames = Address.getNames eventAddress
                match eventNames with
                | [|eventFirstName; _; screenName; layerName; entityName|] ->
                    let entity = Entity [|screenName; layerName; entityName|]
                    match eventFirstName with
                    | "Update" ->
#if DEBUG
                        if Array.contains (Address.head Events.Wildcard) eventNames then
                            Log.debug
                                ("Subscribing to entity update events with a wildcard is not supported. " +
                                 "This will cause a bug where some entity update events are not published.")
#endif
                        World.updateEntityPublishUpdateFlag entity world :> obj
#if !DISABLE_ENTITY_POST_UPDATE
                    | "PostUpdate" ->
#if DEBUG
                        if Array.contains (Address.head Events.Wildcard) eventNames then
                            Log.debug
                                ("Subscribing to entity post-update events with a wildcard is not supported. " +
                                 "This will cause a bug where some entity post-update events are not published.")
#endif
                        World.updateEntityPublishPostUpdateFlag entity world :> obj
#endif
                    | _ -> world :> obj
                | eventNames when eventNames.Length >= 3 ->
                    let eventFirstName = eventNames.[0]
                    let eventSecondName = eventNames.[1]
                    match eventFirstName with
                    | "Change" ->
                        let world =
                            if eventNames.Length = 6 then
                                let entityAddress = rtoa (Array.skip 3 eventNames)
                                let entity = Entity entityAddress
                                match World.tryGetKeyedValue<UMap<Entity Address, int>> EntityChangeCountsId world with
                                | Some entityChangeCounts ->
                                    match entityChangeCounts.TryGetValue entityAddress with
                                    | (true, entityChangeCount) ->
                                        let entityChangeCount = if subscribing then inc entityChangeCount else dec entityChangeCount
                                        let entityChangeCounts =
                                            if entityChangeCount = 0
                                            then UMap.remove entityAddress entityChangeCounts
                                            else UMap.add entityAddress entityChangeCount entityChangeCounts
                                        let world =
                                            if entity.Exists world then
                                                if entityChangeCount = 0 then World.setEntityPublishChangeEvents false entity world
                                                elif entityChangeCount = 1 then World.setEntityPublishChangeEvents true entity world
                                                else world
                                            else world
                                        World.addKeyedValue EntityChangeCountsId entityChangeCounts world
                                    | (false, _) ->
                                        if not subscribing then failwithumf ()
                                        let world = if entity.Exists world then World.setEntityPublishChangeEvents true entity world else world
                                        World.addKeyedValue EntityChangeCountsId (UMap.add entityAddress 1 entityChangeCounts) world
                                | None ->
                                    if not subscribing then failwithumf ()
                                    let entityChangeCounts = if World.getStandAlone world then UMap.makeEmpty Imperative else UMap.makeEmpty Functional
                                    let world = if entity.Exists world then World.setEntityPublishChangeEvents true entity world else world
                                    World.addKeyedValue EntityChangeCountsId (UMap.add entityAddress 1 entityChangeCounts) world
                            else world
                        if  eventSecondName <> "ParentNodeOpt" &&
                            Array.contains (Address.head Events.Wildcard) eventNames then
                            Log.debug "Subscribing to change events with a wildcard is not supported (except for ParentNodeOpt)."
                        world :> obj
                    | _ -> world :> obj
                | _ -> world :> obj

            // init eval F# reach-around
            // TODO: remove duplicated code with the following 4 functions...
            WorldModule.eval <- fun expr localFrame scriptContext world ->
                match expr with
                | Scripting.Unit ->
                    // OPTIMIZATION: don't bother evaluating unit
                    struct (Scripting.Unit, world)
                | _ ->
                    let oldLocalFrame = World.getLocalFrame world
                    let oldScriptContext = World.getScriptContext world
                    World.setLocalFrame localFrame world
                    let world = World.setScriptContext scriptContext world
                    ScriptingSystem.addProceduralBindings (Scripting.AddToNewFrame 1) (seq { yield struct ("self", Scripting.String (scstring scriptContext)) }) world
                    let struct (evaled, world) = World.evalInternal expr world
                    ScriptingSystem.removeProceduralBindings world
                    let world = World.setScriptContext oldScriptContext world
                    World.setLocalFrame oldLocalFrame world
                    struct (evaled, world)

            // init evalMany F# reach-around
            WorldModule.evalMany <- fun exprs localFrame scriptContext world ->
                let oldLocalFrame = World.getLocalFrame world
                let oldScriptContext = World.getScriptContext world
                World.setLocalFrame localFrame world
                let world = World.setScriptContext scriptContext world
                ScriptingSystem.addProceduralBindings (Scripting.AddToNewFrame 1) (seq { yield struct ("self", Scripting.String (scstring scriptContext)) }) world
                let struct (evaleds, world) = World.evalManyInternal exprs world
                ScriptingSystem.removeProceduralBindings world
                let world = World.setScriptContext oldScriptContext world
                World.setLocalFrame oldLocalFrame world
                struct (evaleds, world)

            // init evalWithLogging F# reach-around
            WorldModule.evalWithLogging <- fun expr localFrame scriptContext world ->
                match expr with
                | Scripting.Unit ->
                    // OPTIMIZATION: don't bother evaluating unit
                    struct (Scripting.Unit, world)
                | _ ->
                    let oldLocalFrame = World.getLocalFrame world
                    let oldScriptContext = World.getScriptContext world
                    World.setLocalFrame localFrame world
                    let world = World.setScriptContext scriptContext world
                    ScriptingSystem.addProceduralBindings (Scripting.AddToNewFrame 1) (seq { yield struct ("self", Scripting.String (scstring scriptContext)) }) world
                    let struct (evaled, world) = World.evalWithLoggingInternal expr world
                    ScriptingSystem.removeProceduralBindings world
                    let world = World.setScriptContext oldScriptContext world
                    World.setLocalFrame oldLocalFrame world
                    struct (evaled, world)

            // init evalMany F# reach-around
            WorldModule.evalManyWithLogging <- fun exprs localFrame scriptContext world ->
                let oldLocalFrame = World.getLocalFrame world
                let oldScriptContext = World.getScriptContext world
                World.setLocalFrame localFrame world
                let world = World.setScriptContext scriptContext world
                ScriptingSystem.addProceduralBindings (Scripting.AddToNewFrame 1) (seq { yield struct ("self", Scripting.String (scstring scriptContext)) }) world
                let struct (evaleds, world) = World.evalManyWithLoggingInternal exprs world
                ScriptingSystem.removeProceduralBindings world
                let world = World.setScriptContext oldScriptContext world
                World.setLocalFrame oldLocalFrame world
                struct (evaleds, world)

            // init isSelected F# reach-around
            WorldModule.isSelected <- 
                World.isSelected

            // init sortSubscriptionByElevation F# reach-around
            WorldModule.sortSubscriptionsByElevation <- fun subscriptions worldObj ->
                let world = worldObj :?> World
                EventSystem.sortSubscriptionsBy
                    (fun (simulant : Simulant) _ ->
                        match simulant with
                        | :? Entity as entity -> { SortElevation = entity.GetElevation world; SortPositionY = 0.0f; SortTarget = entity } :> IComparable
                        | :? Layer as layer -> { SortElevation = Constants.Engine.LayerSortPriority; SortPositionY = 0.0f; SortTarget = layer } :> IComparable
                        | :? Screen as screen -> { SortElevation = Constants.Engine.ScreenSortPriority; SortPositionY = 0.0f; SortTarget = screen } :> IComparable
                        | :? Game | :? GlobalSimulantGeneralized -> { SortElevation = Constants.Engine.GameSortPriority; SortPositionY = 0.0f; SortTarget = Simulants.Game } :> IComparable
                        | _ -> failwithumf ())
                    subscriptions
                    world

            // init admitScreenElements F# reach-around
            WorldModule.admitScreenElements <- fun screen world ->
                let entities = World.getLayers screen world |> Seq.map (flip World.getEntities world) |> Seq.concat |> Seq.toArray
                let oldWorld = world
                let entityTree =
                    MutantCache.mutateMutant
                        (fun () -> oldWorld.Dispatchers.RebuildEntityTree oldWorld)
                        (fun entityTree ->
                            for entity in entities do
                                let entityState = World.getEntityState entity world
                                let entityMaxBounds = World.getEntityStateBoundsMax entityState
                                let entityOmnipresent = entityState.Omnipresent || entityState.Absolute
                                SpatialTree.addElement entityOmnipresent entityMaxBounds entity entityTree
                            entityTree)
                        (World.getEntityTree world)
                World.setEntityTree entityTree world
                
            // init evictScreenElements F# reach-around
            WorldModule.evictScreenElements <- fun screen world ->
                let entities = World.getLayers screen world |> Seq.map (flip World.getEntities world) |> Seq.concat |> Seq.toArray
                let oldWorld = world
                let entityTree =
                    MutantCache.mutateMutant
                        (fun () -> oldWorld.Dispatchers.RebuildEntityTree oldWorld)
                        (fun entityTree ->
                            for entity in entities do
                                let entityState = World.getEntityState entity world
                                let entityMaxBounds = World.getEntityStateBoundsMax entityState
                                let entityOmnipresent = entityState.Omnipresent || entityState.Absolute
                                SpatialTree.removeElement entityOmnipresent entityMaxBounds entity entityTree
                            entityTree)
                        (World.getEntityTree world)
                World.setEntityTree entityTree world

            // init registerScreenPhysics F# reach-around
            WorldModule.registerScreenPhysics <- fun screen world ->
                let entities =
                    World.getLayers screen world |>
                    Seq.map (flip World.getEntities world) |>
                    Seq.concat |>
                    Seq.toArray
                Array.fold (fun world (entity : Entity) ->
                    World.registerEntityPhysics entity world)
                    world entities

            // init unregisterScreenPhysics F# reach-around
            WorldModule.unregisterScreenPhysics <- fun screen world ->
                let entities =
                    World.getLayers screen world |>
                    Seq.map (flip World.getEntities world) |>
                    Seq.concat |>
                    Seq.toArray
                Array.fold (fun world (entity : Entity) ->
                    World.unregisterEntityPhysics entity world)
                    world entities

            // init bind5 F# reach-around
            WorldModule.bind5 <- fun simulant left right world ->
                let leftFixup =
                    if isNull (left.This :> obj) then
                        Lens.make
                            left.Name
                            (fun world ->
                                match World.tryGetProperty left.Name simulant world with
                                | Some property -> property.PropertyValue
                                | None -> failwithumf ())
                            (fun propertyValue world ->
                                match World.tryGetProperty left.Name simulant world with
                                | Some property -> snd (World.trySetProperty left.Name { property with PropertyValue = propertyValue } simulant world)
                                | None -> world)
                            simulant
                    else Lens.make left.Name left.GetWithoutValidation (Option.get left.SetOpt) simulant
                let rightFixup = { Lens.makeReadOnly right.Name right.GetWithoutValidation right.This with ValidateOpt = right.ValidateOpt }
                let world = tryPropagateByLens leftFixup rightFixup world // propagate immediately to start things out synchronized
                let propertyBindingId = Gen.id
                let propertyAddress = PropertyAddress.make rightFixup.Name rightFixup.This
                let world = World.monitor (fun _ world -> (Cascade, unbind propertyBindingId propertyAddress world)) (Events.Unregistering --> simulant.SimulantAddress) simulant world
                let world = World.monitor (fun _ world -> (Cascade, tryPropagate simulant leftFixup rightFixup world)) (Events.Register --> right.This.SimulantAddress) simulant world
                let world =
                    match right.This with
                    | :? Entity as entity ->
                        match World.tryGetKeyedValue<UMap<Entity Address, int>> EntityBindingCountsId world with
                        | Some entityBindingCounts ->
                            match UMap.tryFind entity.EntityAddress entityBindingCounts with
                            | Some entityBindingCount ->
                                let entityBindingCount = inc entityBindingCount
                                let entityBindingCounts = UMap.add entity.EntityAddress entityBindingCount entityBindingCounts
                                let world = if entityBindingCount = 1 && entity.Exists world then World.setEntityPublishChangeBindings true entity world else world
                                World.addKeyedValue EntityBindingCountsId entityBindingCounts world
                            | None ->
                                let entityBindingCounts = UMap.add entity.EntityAddress 1 entityBindingCounts
                                let world = if entity.Exists world then World.setEntityPublishChangeBindings true entity world else world
                                World.addKeyedValue EntityBindingCountsId entityBindingCounts world
                        | None ->
                            let entityBindingCounts = if World.getStandAlone world then UMap.makeEmpty Imperative else UMap.makeEmpty Functional
                            let entityBindingCounts = UMap.add entity.EntityAddress 1 entityBindingCounts
                            let world = if entity.Exists world then World.setEntityPublishChangeBindings true entity world else world
                            World.addKeyedValue EntityBindingCountsId entityBindingCounts world
                    | _ -> world
                match world.PropertyBindingsMap.TryGetValue propertyAddress with
                | (true, propertyBindings) ->
                    let propertyBindings = UMap.add propertyBindingId { PBLeft = leftFixup; PBRight = rightFixup } propertyBindings
                    let propertyBindingsMap = UMap.add propertyAddress propertyBindings world.PropertyBindingsMap
                    World.choose { world with PropertyBindingsMap = propertyBindingsMap }
                | (false, _) ->
                    let config = if World.getStandAlone world then Imperative else Functional
                    let propertyBindings = UMap.makeEmpty config
                    let propertyBindings = UMap.add propertyBindingId { PBLeft = leftFixup; PBRight = rightFixup } propertyBindings
                    let propertyBindingsMap = UMap.add propertyAddress propertyBindings world.PropertyBindingsMap
                    World.choose { world with PropertyBindingsMap = propertyBindingsMap }

            // init miscellaneous reach-arounds
            WorldModule.register <- fun simulant world -> World.register simulant world
            WorldModule.unregister <- fun simulant world -> World.unregister simulant world
            WorldModule.expandContent <- fun setScreenSplash content origin owner parent world -> World.expandContent setScreenSplash content origin owner parent world
            WorldModule.destroyImmediate <- fun simulant world -> World.destroyImmediate simulant world
            WorldModule.destroy <- fun simulant world -> World.destroy simulant world
            WorldModule.trySignalFacet <- fun signalObj facetName simulant world -> World.trySignalFacet signalObj facetName simulant world
            WorldModule.trySignal <- fun signalObj simulant world -> World.trySignal signalObj simulant world

            // init debug view F# reach-arounds
            Debug.World.viewGame <- fun world -> Debug.Game.view (world :?> World)
            Debug.World.viewScreen <- fun screen world -> Debug.Screen.view (screen :?> Screen) (world :?> World)
            Debug.World.viewLayer <- fun layer world -> Debug.Layer.view (layer :?> Layer) (world :?> World)
            Debug.World.viewEntity <- fun entity world -> Debug.Entity.view (entity :?> Entity) (world :?> World)

            // init scripting
            World.initScripting ()
            WorldBindings.initBindings ()

            // init vsync
            Vsync.Init nuConfig.RunSynchronously

            // init event world caching
            EventSystemDelegate.setEventAddressCaching true

            // mark init flag
            Initialized <- true

[<AutoOpen; ModuleBinding>]
module WorldModule3 =

    type World with

        static member private pairWithName source =
            (getTypeName source, source)

        static member private makeDefaultGameDispatcher () =
            World.pairWithName (GameDispatcher ())

        static member private makeDefaultScreenDispatchers () =
            Map.ofList [World.pairWithName (ScreenDispatcher ())]

        static member private makeDefaultLayerDispatchers () =
            Map.ofList [World.pairWithName (LayerDispatcher ())]

        static member private makeDefaultEntityDispatchers () =
            // TODO: consider if we should reflectively generate these
            Map.ofListBy World.pairWithName $
                [EntityDispatcher ()
                 StaticSpriteDispatcher () :> EntityDispatcher
                 AnimatedSpriteDispatcher () :> EntityDispatcher
                 NodeDispatcher () :> EntityDispatcher
                 BasicEmitterDispatcher () :> EntityDispatcher
                 EffectDispatcher () :> EntityDispatcher
                 GuiDispatcher () :> EntityDispatcher
                 ButtonDispatcher () :> EntityDispatcher
                 LabelDispatcher () :> EntityDispatcher
                 TextDispatcher () :> EntityDispatcher
                 ToggleDispatcher () :> EntityDispatcher
                 FpsDispatcher () :> EntityDispatcher
                 FeelerDispatcher () :> EntityDispatcher
                 FillBarDispatcher () :> EntityDispatcher
                 BlockDispatcher () :> EntityDispatcher
                 BoxDispatcher () :> EntityDispatcher
                 CharacterDispatcher () :> EntityDispatcher
                 TileMapDispatcher () :> EntityDispatcher
                 TmxMapDispatcher () :> EntityDispatcher]

        static member private makeDefaultFacets () =
            // TODO: consider if we should reflectively generate these
            Map.ofListBy World.pairWithName $
                [NodeFacet () :> Facet
                 BasicEmitterFacet () :> Facet
                 EffectFacet () :> Facet
                 ScriptFacet () :> Facet
                 TextFacet () :> Facet
                 RigidBodyFacet () :> Facet
                 JointFacet () :> Facet
                 TileMapFacet () :> Facet
                 TmxMapFacet () :> Facet
                 StaticSpriteFacet () :> Facet
                 AnimatedSpriteFacet () :> Facet]

        /// Make an empty world.
        static member makeEmpty (config : WorldConfig) =

            // ensure game engine is initialized
            Nu.init config.NuConfig

            // make the default plug-in
            let plugin = NuPlugin ()

            // make the world's event delegate
            let eventDelegate =
                let eventTracing = Core.getEventTracing ()
                let eventTracerOpt = if eventTracing then Some (Log.remark "Event") else None // NOTE: lambda expression is duplicated in multiple places...
                let eventFilter = Core.getEventFilter ()
                let globalSimulant = Simulants.Game
                let globalSimulantGeneralized = { GsgAddress = atoa globalSimulant.GameAddress }
                let eventConfig = if config.StandAlone then Imperative else Functional
                EventSystemDelegate.make eventTracerOpt eventFilter globalSimulant globalSimulantGeneralized eventConfig

            // make the default game dispatcher
            let defaultGameDispatcher = World.makeDefaultGameDispatcher ()

            // make the world's dispatchers
            let dispatchers =
                { GameDispatchers = Map.ofList [defaultGameDispatcher]
                  ScreenDispatchers = World.makeDefaultScreenDispatchers ()
                  LayerDispatchers = World.makeDefaultLayerDispatchers ()
                  EntityDispatchers = World.makeDefaultEntityDispatchers ()
                  Facets = World.makeDefaultFacets ()
                  TryGetExtrinsic = World.tryGetExtrinsic
                  UpdateEntityInEntityTree = World.updateEntityInEntityTree
                  RebuildEntityTree = World.rebuildEntityTree }

            // make the world's subsystems
            let subsystems =
                { PhysicsEngine = MockPhysicsEngine.make ()
                  Renderer = MockRenderer.make ()
                  AudioPlayer = MockAudioPlayer.make () }

            // make the world's scripting environment
            let scriptingEnv = Scripting.Env.make ()

            // make the world's ambient state
            let ambientState =
                let overlayRoutes = World.dispatchersToOverlayRoutes dispatchers.EntityDispatchers
                let overlayRouter = OverlayRouter.make overlayRoutes
                let symbolStore = SymbolStore.makeEmpty ()
                AmbientState.make config.StandAlone 1L (Metadata.makeEmpty ()) overlayRouter Overlayer.empty symbolStore None

            // make the world's spatial tree
            let spatialTree = World.makeEntityTree ()

            // make the world
            let world = World.make plugin eventDelegate dispatchers subsystems scriptingEnv ambientState spatialTree (snd defaultGameDispatcher)

            // finally, register the game
            World.registerGame world

        /// Make a default world with a default screen, layer, and entity, such as for testing.
        static member makeDefault () =
            let worldConfig = WorldConfig.defaultConfig
            let world = World.makeEmpty worldConfig
            let world = World.createScreen (Some Simulants.DefaultScreen.Name) world |> snd
            let world = World.createLayer (Some Simulants.DefaultLayer.Name) Simulants.DefaultScreen world |> snd
            let world = World.createEntity (Some Simulants.DefaultEntity.Name) DefaultOverlay Simulants.DefaultLayer world |> snd
            world

        /// Attempt to make the world, returning either a Right World on success, or a Left string
        /// (with an error message) on failure.
        static member tryMake sdlDeps config (plugin : NuPlugin) =

            // ensure game engine is initialized
            Nu.init config.NuConfig

            // attempt to create asset graph
            match AssetGraph.tryMakeFromFile Assets.Global.AssetGraphFilePath with
            | Right assetGraph ->

                // make the world's event system
                let eventSystem =
                    let eventTracing = Core.getEventTracing ()
                    let eventTracerOpt = if eventTracing then Some (Log.remark "Event") else None
                    let eventFilter = Core.getEventFilter ()
                    let globalSimulant = Simulants.Game
                    let globalSimulantGeneralized = { GsgAddress = atoa globalSimulant.GameAddress }
                    let eventConfig = if config.StandAlone then Imperative else Functional
                    EventSystemDelegate.make eventTracerOpt eventFilter globalSimulant globalSimulantGeneralized eventConfig
                    
                // make plug-in facets and dispatchers
                let pluginFacets = plugin.Birth<Facet> ()
                let pluginGameDispatchers = plugin.Birth<GameDispatcher> ()
                let pluginScreenDispatchers = plugin.Birth<ScreenDispatcher> ()
                let pluginLayerDispatchers = plugin.Birth<LayerDispatcher> ()
                let pluginEntityDispatchers = plugin.Birth<EntityDispatcher> ()

                // make the default game dispatcher
                let defaultGameDispatcher = World.makeDefaultGameDispatcher ()

                // make the world's dispatchers
                let dispatchers =
                    { GameDispatchers = Map.addMany pluginGameDispatchers (Map.ofList [defaultGameDispatcher])
                      ScreenDispatchers = Map.addMany pluginScreenDispatchers (World.makeDefaultScreenDispatchers ())
                      LayerDispatchers = Map.addMany pluginLayerDispatchers (World.makeDefaultLayerDispatchers ())
                      EntityDispatchers = Map.addMany pluginEntityDispatchers (World.makeDefaultEntityDispatchers ())
                      Facets = Map.addMany pluginFacets (World.makeDefaultFacets ())
                      TryGetExtrinsic = World.tryGetExtrinsic
                      UpdateEntityInEntityTree = World.updateEntityInEntityTree
                      RebuildEntityTree = World.rebuildEntityTree }

                // look up the active game dispather
                let activeGameDispatcherType = if config.StandAlone then plugin.GetGameDispatcher () else typeof<GameDispatcher>
                let activeGameDispatcher = Map.find activeGameDispatcherType.Name dispatchers.GameDispatchers

                // make the world's subsystems
                let subsystems =
                    let physicsEngine =
                        AetherPhysicsEngine.make Constants.Physics.GravityDefault
                    let renderer =
                        match SdlDeps.getRenderContextOpt sdlDeps with
                        | Some renderContext -> SdlRenderer.make renderContext :> Renderer
                        | None -> MockRenderer.make () :> Renderer
                    renderer.EnqueueMessage (HintRenderPackageUseMessage Assets.Default.PackageName) // enqueue default package hint
                    let audioPlayer =
                        if SDL.SDL_WasInit SDL.SDL_INIT_AUDIO <> 0u
                        then SdlAudioPlayer.make () :> AudioPlayer
                        else MockAudioPlayer.make () :> AudioPlayer
                    audioPlayer.EnqueueMessage (HintAudioPackageUseMessage Assets.Default.PackageName) // enqueue default package hint
                    { PhysicsEngine = physicsEngine
                      Renderer = renderer
                      AudioPlayer = audioPlayer }

                // attempt to make the overlayer
                let intrinsicOverlays = World.makeIntrinsicOverlays dispatchers.Facets dispatchers.EntityDispatchers
                match Overlayer.tryMakeFromFile intrinsicOverlays Assets.Global.OverlayerFilePath with
                | Right overlayer ->

                    // make the world's scripting environment
                    let scriptingEnv = Scripting.Env.make ()

                    // make the world's ambient state
                    let ambientState =
                        let assetMetadataMap = Metadata.make assetGraph
                        let intrinsicOverlayRoutes = World.dispatchersToOverlayRoutes dispatchers.EntityDispatchers
                        let userOverlayRoutes = plugin.MakeOverlayRoutes ()
                        let overlayRoutes = intrinsicOverlayRoutes @ userOverlayRoutes
                        let overlayRouter = OverlayRouter.make overlayRoutes
                        let symbolStore = SymbolStore.makeEmpty ()
                        AmbientState.make config.StandAlone config.TickRate assetMetadataMap overlayRouter overlayer symbolStore (Some sdlDeps)

                    // make the world's spatial tree
                    let spatialTree = World.makeEntityTree ()

                    // make the world
                    let world = World.make plugin eventSystem dispatchers subsystems scriptingEnv ambientState spatialTree activeGameDispatcher

                    // add the keyed values
                    let (kvps, world) = plugin.MakeKeyedValues world
                    let world = List.fold (fun world (key, value) -> World.addKeyedValue key value world) world kvps

                    // try to load the prelude for the scripting language
                    match World.tryEvalPrelude world with
                    | Right struct (_, world) ->

                        // register the game
                        let world = World.registerGame world

#if DEBUG
                        // attempt to hookup the console if debugging
                        let world = WorldConsole.tryHookUp world |> snd
#endif

                        // fin
                        Right world
                    
                    // forward error messages
                    | Left struct (error, _) -> Left error
                | Left error -> Left error
            | Left error -> Left error

        /// Run the game engine as a stand-alone application.
        static member run worldConfig plugin =
            match SdlDeps.attemptMake worldConfig.SdlConfig with
            | Right sdlDeps ->
                use sdlDeps = sdlDeps // bind explicitly to dispose automatically
                match World.tryMake sdlDeps worldConfig plugin with
                | Right world -> World.run4 tautology sdlDeps Live world
                | Left error -> Log.trace error; Constants.Engine.FailureExitCode
            | Left error -> Log.trace error; Constants.Engine.FailureExitCode