-- The market station (weenie 99002) in the Marketplace hub, 4 m west of the marketplace portal's drop point
-- (cell 0x016C01BC, origin 49.206, -31.935, 0.005, the spot MarketplaceDrop uses in Player_Location.cs).
-- Its guid sits high in the landblock's range, clear of the retail instances.
DELETE FROM `landblock_instance` WHERE `guid` = 0x7016CF00;

-- the hub's cell 0x016C01BC; identity rotation
INSERT INTO `landblock_instance` (`guid`, `weenie_Class_Id`, `obj_Cell_Id`, `origin_X`, `origin_Y`, `origin_Z`, `angles_W`, `angles_X`, `angles_Y`, `angles_Z`, `is_Link_Child`)
VALUES (0x7016CF00, 99002, 0x016C01BC, 45.206, -31.935, 0.005, 1, 0, 0, 0, 0);
