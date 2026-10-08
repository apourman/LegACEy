-- The Vault chest in Yaraq: the first plugin station (plugin-stations spec, ticket 02).
-- Weenie 99001 is a Generic object, not a Chest: Chest.ActOnUse opens the container, while GenericObject.ActOnUse
-- starts the station session when LegaceyStation is set. Its model is the retail Large Chest (weenie 1271): Setup 0x0200007C, Icon 0x06001020.
-- Instance 0x77D6405E stands 4 m east of the Yaraq Life Stone (landblock 0x7D64, guid 0x77D64040, cell 0x7D640000, origin 37.7105, 105.788, 12.005).
-- Safe to run more than once: the instance and the weenie are deleted first (the weenie's property rows cascade).

DELETE FROM `landblock_instance` WHERE `guid` = 0x77D6405E;
DELETE FROM `weenie` WHERE `class_Id` = 99001;

-- type 1 is WeenieType.Generic
INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`)
VALUES (99001, 'legacey_vault_chest', 1);

-- PropertyDataId: Setup = 1, Icon = 8
INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (99001, 1, 0x0200007C)
     , (99001, 8, 0x06001020);

-- PropertyString: Name = 1, LegaceyStation = 9008 (server-only)
INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (99001, 1, 'Vault')
     , (99001, 9008, 'vault');

-- PropertyBool: Stuck = 1 (cannot be picked up)
INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (99001, 1, 1);

-- PropertyFloat: UseRadius = 54 (the retail Large Chest value)
INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (99001, 54, 1);

-- PropertyInt: ItemUseable = 16 (48 = ViewedRemote, as the retail Large Chest)
INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (99001, 16, 48);

-- landblock 0x7D64 (Yaraq), outdoor cell 0x7D640000; identity rotation
INSERT INTO `landblock_instance` (`guid`, `weenie_Class_Id`, `obj_Cell_Id`, `origin_X`, `origin_Y`, `origin_Z`, `angles_W`, `angles_X`, `angles_Y`, `angles_Z`, `is_Link_Child`)
VALUES (0x77D6405E, 99001, 0x7D640000, 41.7105, 105.788, 12.005, 1, 0, 0, 0, 0);
