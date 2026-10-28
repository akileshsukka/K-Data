/*
  Unit 39 - dashboard categories and Ready For Preservation (template v4), from the engineer's file of 26 Oct 2026.
  Run AFTER the app has applied migration Phase7DashboardCategoryCodes (categories have their Code).

  @Commit = 0 : shows what would change and rolls everything back (default).
  @Commit = 1 : applies it. Every asset change is written to ChangeLog (Source = Migration), like an app save.

  65 equipment: 29 Hold, 15 N/A, 21 without category; 16 Ready For Preservation = Yes.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit bit = 0;
DECLARE @ChangedBy nvarchar(256) = N'Data Engineering (Unit 39 v4 seed)';
DECLARE @Now datetime2 = SYSUTCDATETIME();
DECLARE @Batch uniqueidentifier = NEWID();

DECLARE @UnitId int = (SELECT Id FROM [Unit] WHERE Number = 39);
IF @UnitId IS NULL THROW 50001, 'Unit 39 not found.', 1;

-- Dashboard notes of each category (Action By, Email Action, EnerMech Recommendation, Note)
DECLARE @Cat TABLE (Section nvarchar(20), Code nvarchar(10), ActionBy nvarchar(200), EmailAction nvarchar(200),
                    Recommendation nvarchar(1000), Note nvarchar(1000));
INSERT INTO @Cat VALUES
    (N'Hold', N'Cat1', N'PF/DNV', N'Done(16&17, 22 Oct 26)', N'Preservation Not Requried', N'Email 26 Oct: Petrofac confirmed these are tube bundles'),
    (N'Hold', N'Cat2', N'CPY Ops/PF', N'Done(16 Oct 26)', N'Only cleaning required, No preservation required', N'EnerMech: Boil out cleaning proposed to Petrofac'),
    (N'Hold', N'Cat3', N'PF/DNV', N'Done(16 Oct 26)', N'Preservation Not Requried', NULL),
    (N'Hold', N'Cat4', N'CPY Ops/PF', N'Done(16 Oct 26)', N'Preservation Not Requried', NULL),
    (N'Hold', N'Cat5', N'PF', N'Done(16 Oct 26)', N'Installation complete, internal work on-going', N'Installation complete, internal work on-going'),
    (N'NotApplicable', N'Cat1', N'CPY Ops/PF', N'Done(16 Oct 26)', N'Preservation Not Requried', NULL),
    (N'NotApplicable', N'Cat2', N'Info', N'Done(16 Oct 26)', N'Preservation Not Applicable', NULL);

IF EXISTS (SELECT 1 FROM @Cat c WHERE NOT EXISTS (
        SELECT 1 FROM DashboardCategory d WHERE d.UnitId = @UnitId AND d.Section = c.Section AND d.Code = c.Code AND d.IsActive = 1))
BEGIN
    SELECT c.Section, c.Code AS MissingCode FROM @Cat c WHERE NOT EXISTS (
        SELECT 1 FROM DashboardCategory d WHERE d.UnitId = @UnitId AND d.Section = c.Section AND d.Code = c.Code AND d.IsActive = 1);
    THROW 50002, 'Some Unit 39 categories are missing or have no Code. Nothing was changed.', 1;
END;

-- Equipment: K Data Key (Asset.Id), tag for reference, category, ready flag
DECLARE @Seed TABLE (AssetId uniqueidentifier PRIMARY KEY, Tag nvarchar(60), Section nvarchar(20), Code nvarchar(10), Ready nvarchar(10));
INSERT INTO @Seed VALUES
    ('58890a91-859b-4651-8e70-a01aef75b458', N'39-E-101', N'Hold', N'Cat4', N'No'),
    ('ce058e11-98b2-4f74-8104-8a2f55bd0d76', N'39-E-101 (S/N 27462/27461)', N'Hold', N'Cat1', N'No'),
    ('d098a355-993b-4594-8cdc-4fb82f59950b', N'39-E-102', N'Hold', N'Cat4', N'No'),
    ('139329e8-9b2e-4fd1-849c-8dd1a9246dcd', N'39-E-102 (S/N 27490/27489)', N'Hold', N'Cat1', N'No'),
    ('7822b5d8-3d09-4700-8ec8-ae186bcb59dd', N'39-E-102 (S/N 27492/27491)', N'Hold', N'Cat1', N'No'),
    ('d0a71423-7d6b-4b62-a2d7-a7720353f89a', N'39-E-102 (S/N 27494/27493)', N'Hold', N'Cat1', N'No'),
    ('6f8b54e8-97a9-4cca-a2ad-da3ec8fb04a6', N'39-E-102 (S/N 27496/27495)', N'Hold', N'Cat1', N'No'),
    ('355b4016-3810-4d6e-b607-3992bcb945e7', N'39-E-102 (S/N 27498/27497)', N'Hold', N'Cat1', N'No'),
    ('39c9ff21-55f9-4792-b97c-18f5a2d7fae5', N'39-E-102 / 39-E-107 / 39-E-104 (5 units)', N'Hold', N'Cat1', N'No'),
    ('0c0d897f-746c-462c-8e53-7bbdcdc66f84', N'39-E-103A', NULL, NULL, N'Yes'),
    ('49563143-eea4-480c-994b-d583f37c0254', N'39-E-103B', NULL, NULL, N'Yes'),
    ('4cf72b4f-eba2-4391-9a88-9f129fd3239c', N'39-E-104', NULL, NULL, N'Yes'),
    ('acfd5c56-5f0f-4ed9-a026-0d04a74d83ab', N'39-E-104 (S/N 27516/27515)', N'Hold', N'Cat1', N'No'),
    ('ac4e7219-e32d-461b-b570-7870b567d003', N'39-E-104 (S/N 27518/27517)', N'Hold', N'Cat1', N'No'),
    ('d3a2ce8a-e004-4bcc-a21d-ca8944054338', N'39-E-105A', N'NotApplicable', N'Cat1', N'No'),
    ('c4715745-e186-4480-9464-61cad6746af5', N'39-E-105B', N'NotApplicable', N'Cat1', N'No'),
    ('9da5ea7e-7ac2-4e8a-85c0-591b10fa2422', N'39-E-107', NULL, NULL, N'Yes'),
    ('5d97ec81-2bad-4302-a9c6-4bb9caa72809', N'39-E-108', N'Hold', N'Cat4', N'No'),
    ('6f024577-ee9e-4849-928d-036e05b0a895', N'39-E-109', NULL, NULL, N'Yes'),
    ('60e6b22a-78fb-45c1-8bb1-d47494166e7c', N'39-E-109 (S/N 33047/33046)', N'Hold', N'Cat1', N'No'),
    ('c56dafc1-654d-44a3-acac-c478d01e95f9', N'39-E-109 (S/N 33049/33048)', N'Hold', N'Cat1', N'No'),
    ('7b5d859f-974a-4dd8-a94d-0d44e896c279', N'39-F-101A', NULL, NULL, N'No'),
    ('f8ed0f75-ed3c-4d07-bfb8-23df42b49605', N'39-F-101B', NULL, NULL, N'No'),
    ('9861ab4d-87db-4dea-954e-50fddc950254', N'39-F-102', NULL, NULL, N'Yes'),
    ('1691e6a1-2ee5-47f1-81c4-450e450b3b42', N'39-F-103', NULL, NULL, N'Yes'),
    ('4d98beb0-787e-450b-b11e-61b806ac63ba', N'39-F-106', NULL, NULL, N'Yes'),
    ('7e16414c-a734-4c6e-b468-a9c752f710f6', N'39-F-107', NULL, NULL, N'Yes'),
    ('ac4ab108-3446-4cc0-88c2-2ef4fd963637', N'39-SU-101', N'Hold', N'Cat2', N'No'),
    ('adfae86a-9ab4-41de-9c1b-e84ea72e69e3', N'39-SU-102', N'Hold', N'Cat2', N'No'),
    ('e12fec72-399f-4275-bb31-24d7dc322069', N'39-SU-102-V01', N'Hold', N'Cat2', N'No'),
    ('17672706-15e2-44c9-85ff-1c3cdfa0d5aa', N'39-SU-102-V02', N'Hold', N'Cat2', N'No'),
    ('1a9bf1c1-55d4-4c65-acaa-ca83a02625fa', N'39-TK-102', N'Hold', N'Cat3', N'No'),
    ('73c3d8c4-929f-4264-ab1c-4266d654ed5d', N'39-V-101', NULL, NULL, N'No'),
    ('751eda2e-8018-4cff-9e04-cf5770ef5da1', N'39-V-102', N'Hold', N'Cat5', N'No'),
    ('f4c5ab76-3931-4c7f-acfb-1a04aa8c5af7', N'39-V-103', N'Hold', N'Cat3', N'No'),
    ('6d986aac-6e29-45b3-8e6f-4275bf9af845', N'39-V-104', N'Hold', N'Cat3', N'No'),
    ('b144e503-21bf-495a-9806-9ca559ca5d0f', N'39-V-105', N'NotApplicable', N'Cat1', N'No'),
    ('51872d18-a82a-45ec-8ce3-013676670423', N'39-V-106', N'Hold', N'Cat3', N'No'),
    ('2a95a9eb-0fea-463c-b0ec-fb8768ef8397', N'39-V-107', N'Hold', N'Cat2', N'No'),
    ('fc64896d-c6d8-4d3c-be48-60caff653413', N'39-V-108', NULL, NULL, N'No'),
    ('85784983-3717-440e-8f28-9a119c52567c', N'39-V-109', N'NotApplicable', N'Cat1', N'No'),
    ('16acee31-5af7-4651-ac71-043011a3479c', N'39-V-111', N'Hold', N'Cat2', N'No'),
    ('951c1215-be56-4e57-bfff-162483351d1a', N'39-V-112', N'NotApplicable', N'Cat1', N'No'),
    ('7b9eec7c-dcfd-4bee-8cfd-559392b3c3b3', N'39-V-119', N'Hold', N'Cat2', N'No'),
    ('748cb2b7-22cd-4984-bece-726569ddfa10', N'39-V-120', NULL, NULL, N'No'),
    ('e2a41397-bc12-4da8-a005-2ac5917bfb50', N'39-P-101A', N'NotApplicable', N'Cat2', N'No'),
    ('ef8f3cd9-41f0-4794-95a5-3d4100a6bd7f', N'39-P-101B', N'NotApplicable', N'Cat2', N'No'),
    ('d61b49bf-92cd-4351-a5f9-ea05b90b87f5', N'39-P-102A', N'NotApplicable', N'Cat2', N'No'),
    ('eb4f1021-ba0a-4d19-9d8f-2a0f98cf24ba', N'39-P-102B', N'NotApplicable', N'Cat2', N'No'),
    ('4be5ed30-bea8-47cd-a424-dd4f535da960', N'39-P-103', N'NotApplicable', N'Cat1', N'No'),
    ('d89eb104-ab1c-4310-86ce-2f46f693fe01', N'39-P-106A', N'NotApplicable', N'Cat1', N'No'),
    ('f4bb578f-9620-4e11-8db1-f88d2607d0d4', N'39-P-106B', N'NotApplicable', N'Cat1', N'No'),
    ('88cd9a1a-1a5f-4284-bdcd-ca374a8c2392', N'39-P-107', NULL, NULL, N'Yes'),
    ('803ea08f-321b-4c7b-9ba1-5bb41beaee6b', N'39-P-108', N'NotApplicable', N'Cat1', N'No'),
    ('625176de-0af7-4279-ad2c-99258fc58944', N'39-P-109', N'Hold', N'Cat2', N'No'),
    ('be0eba91-8f5d-44a1-bab1-264da9e8f2b7', N'39-P-110', N'Hold', N'Cat2', N'No'),
    ('b4fdebbc-c805-44f6-a9d2-ca0b74e287c0', N'39-P-112', N'NotApplicable', N'Cat1', N'No'),
    ('18588c20-ca35-45f4-876a-a6f14cdedd8d', N'39-P-114', NULL, NULL, N'Yes'),
    ('f87d1bd0-6dbc-437e-9eb8-5af5e5cdb61f', N'39-P-115', NULL, NULL, N'Yes'),
    ('4e1256e4-ec6a-4c54-8e54-74d34ee96fae', N'39-P-116', NULL, NULL, N'Yes'),
    ('8c942f71-b518-4d2e-8dc8-934b8141dc63', N'39-P-117', NULL, NULL, N'Yes'),
    ('05b5c018-333f-4598-8264-9f4b9f4f6115', N'39-P-118 A/B', N'NotApplicable', N'Cat1', N'No'),
    ('e33e90ba-355a-4974-b259-b2b1e43a4bdb', N'39-P-119', NULL, NULL, N'Yes'),
    ('e55e2ed9-f5fa-4ee6-a4eb-aec05bba8735', N'39-ME-101', N'Hold', N'Cat2', N'No'),
    ('574374f2-7d92-4fed-be41-873449e62322', N'39-ME-102', NULL, NULL, N'Yes');

IF EXISTS (SELECT 1 FROM @Seed s WHERE NOT EXISTS (
        SELECT 1 FROM [Asset] a JOIN AssetPreservation p ON p.AssetId = a.Id WHERE a.Id = s.AssetId AND a.UnitId = @UnitId))
BEGIN
    SELECT s.AssetId, s.Tag AS NotFoundInUnit39 FROM @Seed s WHERE NOT EXISTS (
        SELECT 1 FROM [Asset] a JOIN AssetPreservation p ON p.AssetId = a.Id WHERE a.Id = s.AssetId AND a.UnitId = @UnitId);
    THROW 50003, 'Some keys are not Unit 39 equipment. Nothing was changed.', 1;
END;

BEGIN TRANSACTION;

UPDATE d SET ActionBy = c.ActionBy, EmailAction = c.EmailAction, Recommendation = c.Recommendation, Note = c.Note
FROM DashboardCategory d JOIN @Cat c ON d.UnitId = @UnitId AND d.Section = c.Section AND d.Code = c.Code;

DECLARE @Target TABLE (AssetId uniqueidentifier PRIMARY KEY, Tag nvarchar(60), OldCategoryId int NULL, NewCategoryId int NULL,
                       OldReady nvarchar(10) NULL, NewReady nvarchar(10) NULL);
INSERT INTO @Target
SELECT s.AssetId, s.Tag, p.DashboardCategoryId, d.Id, p.ReadyForPreservation, NULLIF(s.Ready, N'')
FROM @Seed s
JOIN AssetPreservation p ON p.AssetId = s.AssetId
LEFT JOIN DashboardCategory d ON d.UnitId = @UnitId AND d.Section = s.Section AND d.Code = s.Code;

INSERT INTO ChangeLog (EntityType, EntityKey, AssetId, Field, OldValue, NewValue, ChangedAtUtc, ChangedBy, Source, BatchId)
SELECT N'AssetPreservation', LOWER(CONVERT(nvarchar(36), t.AssetId)), t.AssetId, N'DashboardCategoryId',
       CONVERT(nvarchar(20), t.OldCategoryId), CONVERT(nvarchar(20), t.NewCategoryId), @Now, @ChangedBy, N'Migration', @Batch
FROM @Target t WHERE ISNULL(t.OldCategoryId, -1) <> ISNULL(t.NewCategoryId, -1)
UNION ALL
SELECT N'AssetPreservation', LOWER(CONVERT(nvarchar(36), t.AssetId)), t.AssetId, N'ReadyForPreservation',
       t.OldReady, t.NewReady, @Now, @ChangedBy, N'Migration', @Batch
FROM @Target t WHERE ISNULL(t.OldReady, N'') <> ISNULL(t.NewReady, N'');

UPDATE p SET DashboardCategoryId = t.NewCategoryId, ReadyForPreservation = t.NewReady, UpdatedAtUtc = @Now, UpdatedBy = @ChangedBy
FROM AssetPreservation p JOIN @Target t ON t.AssetId = p.AssetId
WHERE ISNULL(t.OldCategoryId, -1) <> ISNULL(t.NewCategoryId, -1) OR ISNULL(t.OldReady, N'') <> ISNULL(t.NewReady, N'');

-- Check: expected Hold 29 (11/10/4/3/1), N/A 15 (11/4), Ready = Yes 16
SELECT d.Section, d.Code, d.Label, COUNT(p.AssetId) AS Equipment
FROM DashboardCategory d
LEFT JOIN AssetPreservation p ON p.DashboardCategoryId = d.Id
WHERE d.UnitId = @UnitId
GROUP BY d.Section, d.Code, d.Label, d.SortOrder
ORDER BY d.Section, d.Code;

SELECT COUNT(*) AS ReadyYes FROM @Seed s JOIN AssetPreservation p ON p.AssetId = s.AssetId WHERE p.ReadyForPreservation = N'Yes';
SELECT COUNT(*) AS ChangeLogRows FROM ChangeLog WHERE BatchId = @Batch;

IF @Commit = 1
BEGIN
    COMMIT;
    PRINT 'Applied.';
END
ELSE
BEGIN
    ROLLBACK;
    PRINT 'Report only (@Commit = 0): nothing was saved.';
END;
