-- The Vault chest: the Vault's plugin station.
-- A Generic object, not a Chest: Chest.ActOnUse opens the container, while GenericObject.ActOnUse
-- starts the station session when LegaceyStation is set. Its model is the retail Large Chest (weenie 1271).
DELETE FROM `weenie` WHERE `class_Id` = 99001;

-- type 1 is WeenieType.Generic
INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`)
VALUES (99001, 'legacey_vault_chest', 1);

-- PropertyDataId: Setup = 1, Icon = 8
INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (99001, 1, 0x0200007C)
     , (99001, 8, 0x06001020);

-- PropertyString: Name = 1, LegaceyStation = 9500 (server-only)
INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (99001, 1, 'Vault')
     , (99001, 9500, 'vault');

-- PropertyBool: Stuck = 1 (cannot be picked up)
INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (99001, 1, 1);

-- PropertyFloat: UseRadius = 54, 1 m
INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (99001, 54, 1);

-- PropertyInt: ItemUseable = 16, 48 = ViewedRemote (as the retail Large Chest)
INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (99001, 16, 48);
