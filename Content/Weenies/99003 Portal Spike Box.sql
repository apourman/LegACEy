-- Portal spike test model (portal-overlay spike): a copy of the retail box (Setup 0x02000267) at new ids with a magenta and
-- yellow checker texture, Setup 0x0200F001 -> GfxObj 0x0100F001 -> Surface 0x0800F001 -> SurfaceTexture 0x0500F001 ->
-- RenderSurface 0x0600F001. Those ids exist only in the spike's test world, whose patch the client's dat overlay serves.
DELETE FROM `weenie` WHERE `class_Id` = 99003;

-- type 1 is WeenieType.Generic
INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`)
VALUES (99003, 'legacey_portal_spike_box', 1);

-- PropertyDataId: Setup = 1
INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (99003, 1, 0x0200F001);

-- PropertyString: Name = 1
INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (99003, 1, 'Portal Spike Box');

-- PropertyBool: Stuck = 1 (cannot be picked up)
INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (99003, 1, 1);

-- PropertyFloat: DefaultScale = 39, three times the retail box (about 5 m tall)
INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (99003, 39, 3);
