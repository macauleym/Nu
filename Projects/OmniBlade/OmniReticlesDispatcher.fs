﻿// Nu Game Engine.
// Copyright (C) Bryan Edds, 2013-2020.

namespace OmniBlade
open Prime
open Nu
open Nu.Declarative
open OmniBlade

[<AutoOpen>]
module ReticlesDispatcher =

    type [<StructuralEquality; NoComparison>] Reticles =
        { Battle : Battle // TODO: P1: let's see if we can make this reference something smaller.
          AimType : AimType }

    type ReticlesCommand =
        | TargetCancel
        | TargetSelect of CharacterIndex

    type Entity with
        member this.GetReticles = this.GetModel<Reticles>
        member this.SetReticles = this.SetModel<Reticles>
        member this.Reticles = this.Model<Reticles> ()
        member this.TargetSelectEvent = Events.TargetSelect --> this

    type ReticlesDispatcher () =
        inherit GuiDispatcher<Reticles, unit, ReticlesCommand> ({ Battle = Battle.empty; AimType = EnemyAim true })

        static member Properties =
            [define Entity.SwallowMouseLeft false
             define Entity.Visible false]

        override this.Command (_, command, rets, world) =
            match command with
            | TargetCancel -> just (World.publishPlus () rets.CancelEvent [] rets true world)
            | TargetSelect index -> just (World.publishPlus index rets.TargetSelectEvent [] rets true world)

        override this.Content (reticles, rets) =
            let buttonName = rets.Name + "+" + "Cancel"
            let button = rets.Parent / buttonName
            [Content.button button.Name
                [Entity.ParentNodeOpt == None
                 Entity.Visible <== rets.Visible
                 Entity.Size == v2 48.0f 48.0f
                 Entity.Position == Constants.Battle.CancelPosition
                 Entity.UpImage == asset Assets.Battle.PackageName "CancelUp"
                 Entity.DownImage == asset Assets.Battle.PackageName "CancelDown"
                 Entity.ClickEvent ==> cmd TargetCancel]
             Content.entities reticles
                (fun reticles -> (reticles.AimType, reticles.Battle))
                (fun (aimType, battle) _ -> Battle.getTargets aimType battle)
                (fun index character world ->
                    let buttonName = rets.Name + "+Reticle+" + scstring index
                    let button = rets.Parent / buttonName
                    Content.button button.Name
                        [Entity.ParentNodeOpt == None
                         Entity.Size == v2 96.0f 96.0f
                         Entity.Center <== character --> fun (character : Character) -> character.CenterOffset
                         Entity.UpImage == asset Assets.Battle.PackageName "ReticleUp"
                         Entity.DownImage == asset Assets.Battle.PackageName "ReticleDown"
                         Entity.ClickEvent ==> cmd (TargetSelect (character.Get world).CharacterIndex)])]