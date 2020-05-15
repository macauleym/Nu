﻿namespace OmniBlade
open Prime
open Nu
open Nu.Declarative
open OmniBlade

[<AutoOpen>]
module OmniGame =

    type [<NoComparison>] OmniModel =
        | Splashing
        | Title
        | Credits
        | Gameplay of FieldModel

    type [<NoComparison>] OmniMessage =
        | Update
        | UpdateFieldModel of FieldModel
        | UpdateBattleModel of BattleModel
        | SetModel of OmniModel

    type [<NoComparison>] OmniCommand =
        | Show of Screen
        | Exit

    type Game with

        member this.GetOmniModel = this.GetModel<OmniModel>
        member this.SetOmniModel = this.SetModel<OmniModel>
        member this.OmniModel = this.Model<OmniModel> ()

    type OmniDispatcher () =
        inherit GameDispatcher<OmniModel, OmniMessage, OmniCommand> (Splashing)

        override this.Register (game, world) =
            let world = World.hintRenderPackageUse Assets.GuiPackageName world
            let world = World.hintAudioPackageUse Assets.GuiPackageName world
            base.Register (game, world)

        override this.Channel (_, _) =
            [Simulants.Game.UpdateEvent => [msg Update]
             Simulants.TitlePlay.ClickEvent => [msg (SetModel (Gameplay FieldModel.empty))]
             Simulants.TitleCredits.ClickEvent => [msg (SetModel Credits)]
             Simulants.CreditsBack.ClickEvent => [msg (SetModel Title)]
             Simulants.FieldBack.ClickEvent => [msg (SetModel Title)]
             Simulants.TitleExit.ClickEvent => [cmd Exit]
             Simulants.Field.FieldModel.ChangeEvent =|> fun evt -> [msg (UpdateFieldModel (evt.Data.Value :?> FieldModel))]
             Simulants.Battle.BattleModel.ChangeEvent =|> fun evt -> [msg (UpdateBattleModel (evt.Data.Value :?> BattleModel))]]

        override this.Message (model, message, _, world) =
            match message with
            | Update ->
                match model with
                | Splashing -> just model
                | Title -> withCmd model (Show Simulants.Title)
                | Credits -> withCmd model (Show Simulants.Credits)
                | Gameplay field ->
                    match field.BattleOpt with
                    | Some battle ->
                        match battle.BattleState with
                        | BattleCease (_, time) ->
                            if World.getTickTime world - time >= 120L
                            then withCmd model (Show Simulants.Field)
                            else withCmd model (Show Simulants.Battle)
                        | _ -> withCmd model (Show Simulants.Battle)
                    | None -> withCmd model (Show Simulants.Field)
            | UpdateFieldModel field ->
                match model with
                | Splashing | Title | Credits -> just model
                | Gameplay _ -> just (Gameplay field)
            | UpdateBattleModel battle ->
                match model with
                | Splashing | Title | Credits -> just model
                | Gameplay field -> just (Gameplay (FieldModel.updateBattleOpt (constant (Some battle)) field))
            | SetModel model -> just model

        override this.Command (_, command, _, world) =
            let world =
                match command with
                | Show screen -> World.transitionScreen screen world
                | Exit -> World.exit world
            just world

        override this.Content (model, _) =
            let titleSong = { Volume = Constants.Audio.DefaultSongVolume; FadeOutMs = Constants.Audio.DefaultFadeOutMs; Song = Assets.TitleSong }
            let battleSong = { Volume = Constants.Audio.DefaultSongVolume; FadeOutMs = Constants.Audio.DefaultFadeOutMs; Song = Assets.BattleSong }
            [Content.screen Simulants.Splash.Name (Splash (Constants.Dissolve.Default, Constants.Splash.Default, Simulants.Title)) [] []
             Content.screenFromLayerFile Simulants.Title.Name (Dissolve (Constants.Dissolve.Default, (Some titleSong))) Assets.TitleLayerFilePath
             Content.screenFromLayerFile Simulants.Credits.Name (Dissolve (Constants.Dissolve.Default, (Some titleSong))) Assets.CreditsLayerFilePath
             Content.screen<FieldDispatcher> Simulants.Field.Name (Dissolve (Constants.Dissolve.Default, None))
                [Screen.FieldModel <== model --> fun model ->
                    match model with
                    | Splashing | Title | Credits -> FieldModel.empty
                    | Gameplay field -> field] []
             Content.screen<BattleDispatcher> Simulants.Battle.Name (Dissolve (Constants.Dissolve.Default, (Some battleSong)))
                [Screen.BattleModel <== model --> fun model ->
                    match model with
                    | Splashing | Title | Credits -> BattleModel.empty
                    | Gameplay field -> Option.getOrDefault BattleModel.empty field.BattleOpt] []]