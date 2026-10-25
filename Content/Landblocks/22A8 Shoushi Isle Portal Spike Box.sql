-- The portal spike box (weenie 99003) in the Shoushi copy of the spike's test world (landblock 0x22A8, ocean in retail), a few metres
-- from the teleport-in spot 0x22A80025 [96 96 25]. Only a server on that world has the landblock; elsewhere it never loads.
DELETE FROM `landblock_instance` WHERE `guid` = 0x722A8F00;

-- outdoor cell 0x22A80024; identity rotation
INSERT INTO `landblock_instance` (`guid`, `weenie_Class_Id`, `obj_Cell_Id`, `origin_X`, `origin_Y`, `origin_Z`, `angles_W`, `angles_X`, `angles_Y`, `angles_Z`, `is_Link_Child`)
VALUES (0x722A8F00, 99003, 0x22A80024, 100, 92, 20.005, 1, 0, 0, 0, 0);
