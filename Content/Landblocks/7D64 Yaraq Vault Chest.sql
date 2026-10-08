-- The Vault chest (weenie 99001) in Yaraq, 4 m east of the Life Stone (guid 0x77D64040, origin 37.7105, 105.788, 12.005).
-- Its guid sits high in the landblock's range, clear of the retail instances (0x77D64000-0x77D6405D).
DELETE FROM `landblock_instance` WHERE `guid` = 0x77D64F00;

-- outdoor cell 0x7D640000; identity rotation
INSERT INTO `landblock_instance` (`guid`, `weenie_Class_Id`, `obj_Cell_Id`, `origin_X`, `origin_Y`, `origin_Z`, `angles_W`, `angles_X`, `angles_Y`, `angles_Z`, `is_Link_Child`)
VALUES (0x77D64F00, 99001, 0x7D640000, 41.7105, 105.788, 12.005, 1, 0, 0, 0, 0);
