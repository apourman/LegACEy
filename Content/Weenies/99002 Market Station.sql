-- The market station: the LegACEy Market window's station, in the Marketplace hub.
-- A Generic object, like the Vault chest (99001): GenericObject.ActOnUse starts the station session when LegaceyStation is set.
-- Its model is the retail Sealed Vault (weenie 35405), so it looks like the chest and never opens.
DELETE FROM `weenie` WHERE `class_Id` = 99002;

-- type 1 is WeenieType.Generic
INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`)
VALUES (99002, 'legacey_market_station', 1);

-- PropertyDataId: Setup = 1, MotionTable = 2, SoundTable = 3, Icon = 8, PhysicsEffectTable = 22
INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (99002, 1, 0x020016A2)
     , (99002, 2, 0x09000185)
     , (99002, 3, 0x20000026)
     , (99002, 8, 0x06003774)
     , (99002, 22, 0x3400002B);

-- PropertyString: Name = 1, LegaceyStation = 9500 (server-only)
INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (99002, 1, 'Market')
     , (99002, 9500, 'market');

-- PropertyBool: Stuck = 1 (cannot be picked up)
INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (99002, 1, 1);

-- PropertyFloat: UseRadius = 54, 1 m
INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (99002, 54, 1);

-- PropertyInt: ItemUseable = 16, 48 = ViewedRemote (as the retail Sealed Vault)
INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (99002, 16, 48);
