BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    ALTER TABLE [Products] ADD [ShippingWeightGrams] int NOT NULL DEFAULT 500;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    ALTER TABLE [Orders] ADD [ShippingQuoteId] uniqueidentifier NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE TABLE [CustomOrderRequests] (
        [Id] uniqueidentifier NOT NULL,
        [CustomerId] uniqueidentifier NOT NULL,
        [ProductId] uniqueidentifier NOT NULL,
        [Quantity] int NOT NULL,
        [Description] nvarchar(4000) NOT NULL,
        [ReferenceImageUrl] nvarchar(2000) NOT NULL,
        [RequestedCompletionDate] datetime2 NULL,
        [ProductionStartedAt] datetime2 NULL,
        [CompletedAt] datetime2 NULL,
        [Status] nvarchar(40) NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedAt] datetime2 NOT NULL,
        [ConcurrencyToken] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_CustomOrderRequests] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_CustomRequest_Quantity] CHECK (Quantity > 0)
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE TABLE [GatewayPaymentIntents] (
        [Id] uniqueidentifier NOT NULL,
        [OrderId] uniqueidentifier NOT NULL,
        [OwnerId] uniqueidentifier NOT NULL,
        [ClientKey] nvarchar(100) NOT NULL,
        [ProviderKey] nvarchar(200) NOT NULL,
        [Amount] decimal(18,2) NOT NULL,
        [Currency] nvarchar(3) NOT NULL,
        [ReturnUrlOrigin] nvarchar(2048) NOT NULL,
        [RedirectUrl] nvarchar(2048) NOT NULL,
        [TransactionCode] nvarchar(100) NOT NULL,
        [Status] nvarchar(40) NOT NULL,
        [ReconciliationStatus] nvarchar(80) NOT NULL,
        [EvidenceReference] nvarchar(500) NOT NULL,
        [ProviderResponse] nvarchar(max) NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UpdatedAtUtc] datetime2 NULL,
        CONSTRAINT [PK_GatewayPaymentIntents] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_GatewayIntent_Amount] CHECK (Amount > 0),
        CONSTRAINT [FK_GatewayPaymentIntents_Orders_OrderId] FOREIGN KEY ([OrderId]) REFERENCES [Orders] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE TABLE [GatewayRefundAttempt] (
        [Id] uniqueidentifier NOT NULL,
        [RefundId] uniqueidentifier NOT NULL,
        [PaymentId] uniqueidentifier NOT NULL,
        [Amount] decimal(18,2) NOT NULL,
        [Status] nvarchar(40) NOT NULL,
        [ProviderTransactionCode] nvarchar(100) NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_GatewayRefundAttempt] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_GatewayRefundAttempt_Payments_PaymentId] FOREIGN KEY ([PaymentId]) REFERENCES [Payments] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_GatewayRefundAttempt_Refunds_RefundId] FOREIGN KEY ([RefundId]) REFERENCES [Refunds] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE TABLE [NotificationOutbox] (
        [Id] uniqueidentifier NOT NULL,
        [EventType] nvarchar(80) NOT NULL,
        [CustomerId] uniqueidentifier NULL,
        [Destination] nvarchar(254) NULL,
        [Channel] int NOT NULL,
        [Subject] nvarchar(200) NOT NULL,
        [PayloadJson] nvarchar(max) NOT NULL,
        [Status] int NOT NULL,
        [Attempts] int NOT NULL,
        [NextAttemptAtUtc] datetime2 NOT NULL,
        [LeasedUntilUtc] datetime2 NULL,
        [LeaseOwner] nvarchar(200) NULL,
        [SentAtUtc] datetime2 NULL,
        [LastError] nvarchar(1000) NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_NotificationOutbox] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE TABLE [PasswordResetToken] (
        [Id] uniqueidentifier NOT NULL,
        [CustomerId] uniqueidentifier NOT NULL,
        [TokenHash] nvarchar(128) NOT NULL,
        [ExpiresAtUtc] datetime2 NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [UsedAtUtc] datetime2 NULL,
        CONSTRAINT [PK_PasswordResetToken] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE TABLE [RateLimitCounters] (
        [Id] uniqueidentifier NOT NULL,
        [BucketKey] nvarchar(128) NOT NULL,
        [WindowStartUtc] datetime2 NOT NULL,
        [Count] int NOT NULL,
        [UpdatedAtUtc] datetime2 NOT NULL,
        CONSTRAINT [PK_RateLimitCounters] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE TABLE [RefreshToken] (
        [Id] uniqueidentifier NOT NULL,
        [CustomerId] uniqueidentifier NOT NULL,
        [TokenHash] nvarchar(128) NOT NULL,
        [FamilyId] uniqueidentifier NOT NULL,
        [ExpiresAtUtc] datetime2 NOT NULL,
        [CreatedAtUtc] datetime2 NOT NULL,
        [RevokedAtUtc] datetime2 NULL,
        [ReplacedByHash] nvarchar(128) NULL,
        [RevocationReason] nvarchar(80) NULL,
        CONSTRAINT [PK_RefreshToken] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE TABLE [ShippingQuotes] (
        [Id] uniqueidentifier NOT NULL,
        [CartFingerprint] nvarchar(64) NOT NULL,
        [CustomerId] uniqueidentifier NOT NULL,
        [Origin] nvarchar(500) NOT NULL,
        [Destination] nvarchar(2000) NOT NULL,
        [Weight] decimal(18,3) NOT NULL,
        [Amount] decimal(18,2) NOT NULL,
        [TrackingNumber] nvarchar(200) NULL,
        [ExpiresAt] datetime2 NOT NULL,
        [UsedAt] datetime2 NULL,
        [OrderId] uniqueidentifier NULL,
        CONSTRAINT [PK_ShippingQuotes] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_ShippingQuotes_Amounts] CHECK (Weight > 0 AND Amount >= 0)
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE TABLE [CustomQuotations] (
        [Id] uniqueidentifier NOT NULL,
        [CustomOrderRequestId] uniqueidentifier NOT NULL,
        [CurrentRevision] int NOT NULL,
        [Status] nvarchar(40) NOT NULL,
        [ExpiresAt] datetime2 NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedAt] datetime2 NOT NULL,
        [ConcurrencyToken] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_CustomQuotations] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_CustomQuotations_CustomOrderRequests_CustomOrderRequestId] FOREIGN KEY ([CustomOrderRequestId]) REFERENCES [CustomOrderRequests] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE TABLE [CustomOrderCheckouts] (
        [Id] uniqueidentifier NOT NULL,
        [CustomQuotationId] uniqueidentifier NOT NULL,
        [QuotationVersion] int NOT NULL,
        [CustomerId] uniqueidentifier NOT NULL,
        [OrderId] uniqueidentifier NOT NULL,
        [IdempotencyKey] nvarchar(100) NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_CustomOrderCheckouts] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_CustomOrderCheckouts_CustomQuotations_CustomQuotationId] FOREIGN KEY ([CustomQuotationId]) REFERENCES [CustomQuotations] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_CustomOrderCheckouts_Orders_OrderId] FOREIGN KEY ([OrderId]) REFERENCES [Orders] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE TABLE [CustomQuotationRevisions] (
        [Id] uniqueidentifier NOT NULL,
        [CustomQuotationId] uniqueidentifier NOT NULL,
        [Version] int NOT NULL,
        [UnitPrice] decimal(18,2) NOT NULL,
        [Quantity] int NOT NULL,
        [TotalAmount] decimal(18,2) NOT NULL,
        [DepositAmount] decimal(18,2) NOT NULL,
        [CompletionDate] datetime2 NOT NULL,
        [ExpiresAt] datetime2 NOT NULL,
        [CreatedBy] uniqueidentifier NOT NULL,
        [AdminNote] nvarchar(2000) NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [Status] nvarchar(40) NOT NULL,
        CONSTRAINT [PK_CustomQuotationRevisions] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_CustomQuote_Amounts] CHECK (UnitPrice >= 0 AND TotalAmount >= 0 AND DepositAmount >= 0 AND CAST(DepositAmount AS DECIMAL(18,2)) <= CAST(TotalAmount AS DECIMAL(18,2)) AND Quantity > 0),
        CONSTRAINT [FK_CustomQuotationRevisions_CustomQuotations_CustomQuotationId] FOREIGN KEY ([CustomQuotationId]) REFERENCES [CustomQuotations] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    EXEC(N'ALTER TABLE [Products] ADD CONSTRAINT [CK_Product_ShippingWeight] CHECK (ShippingWeightGrams > 0)');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE UNIQUE INDEX [IX_CustomOrderCheckouts_CustomerId_IdempotencyKey] ON [CustomOrderCheckouts] ([CustomerId], [IdempotencyKey]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE UNIQUE INDEX [IX_CustomOrderCheckouts_CustomQuotationId] ON [CustomOrderCheckouts] ([CustomQuotationId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE UNIQUE INDEX [IX_CustomOrderCheckouts_OrderId] ON [CustomOrderCheckouts] ([OrderId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE INDEX [IX_CustomOrderRequests_CustomerId_CreatedAt] ON [CustomOrderRequests] ([CustomerId], [CreatedAt]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE UNIQUE INDEX [IX_CustomQuotationRevisions_CustomQuotationId_Version] ON [CustomQuotationRevisions] ([CustomQuotationId], [Version]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE UNIQUE INDEX [IX_CustomQuotations_CustomOrderRequestId] ON [CustomQuotations] ([CustomOrderRequestId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE UNIQUE INDEX [IX_GatewayPaymentIntents_OrderId_ClientKey] ON [GatewayPaymentIntents] ([OrderId], [ClientKey]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE INDEX [IX_GatewayPaymentIntents_OrderId_OwnerId] ON [GatewayPaymentIntents] ([OrderId], [OwnerId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE UNIQUE INDEX [IX_GatewayPaymentIntents_ProviderKey] ON [GatewayPaymentIntents] ([ProviderKey]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_GatewayPaymentIntents_TransactionCode] ON [GatewayPaymentIntents] ([TransactionCode]) WHERE [TransactionCode] <> ''''');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE INDEX [IX_GatewayRefundAttempt_PaymentId] ON [GatewayRefundAttempt] ([PaymentId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE UNIQUE INDEX [IX_GatewayRefundAttempt_RefundId] ON [GatewayRefundAttempt] ([RefundId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE INDEX [IX_NotificationOutbox_LeasedUntilUtc] ON [NotificationOutbox] ([LeasedUntilUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE INDEX [IX_NotificationOutbox_Status_NextAttemptAtUtc] ON [NotificationOutbox] ([Status], [NextAttemptAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE INDEX [IX_PasswordResetToken_CustomerId_ExpiresAtUtc] ON [PasswordResetToken] ([CustomerId], [ExpiresAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE UNIQUE INDEX [IX_PasswordResetToken_TokenHash] ON [PasswordResetToken] ([TokenHash]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE UNIQUE INDEX [IX_RateLimitCounters_BucketKey_WindowStartUtc] ON [RateLimitCounters] ([BucketKey], [WindowStartUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE INDEX [IX_RateLimitCounters_UpdatedAtUtc] ON [RateLimitCounters] ([UpdatedAtUtc]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE INDEX [IX_RefreshToken_CustomerId_FamilyId] ON [RefreshToken] ([CustomerId], [FamilyId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE UNIQUE INDEX [IX_RefreshToken_TokenHash] ON [RefreshToken] ([TokenHash]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    CREATE INDEX [IX_ShippingQuotes_CustomerId_Id] ON [ShippingQuotes] ([CustomerId], [Id]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_ShippingQuotes_OrderId] ON [ShippingQuotes] ([OrderId]) WHERE [OrderId] IS NOT NULL');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261023033757_CompleteCommerceLifecycle'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261023033757_CompleteCommerceLifecycle', N'8.0.22');
END;
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024142218_AddConfirmedPackagingSpecs'
)
BEGIN
    ALTER TABLE [Products] DROP CONSTRAINT [CK_Product_ShippingWeight];
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024142218_AddConfirmedPackagingSpecs'
)
BEGIN
    DECLARE @var0 sysname;
    SELECT @var0 = [d].[name]
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Products]') AND [c].[name] = N'ShippingWeightGrams');
    IF @var0 IS NOT NULL EXEC(N'ALTER TABLE [Products] DROP CONSTRAINT [' + @var0 + '];');
    ALTER TABLE [Products] ALTER COLUMN [ShippingWeightGrams] int NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024142218_AddConfirmedPackagingSpecs'
)
BEGIN
    ALTER TABLE [Products] ADD [PackageHeightCm] decimal(18,2) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024142218_AddConfirmedPackagingSpecs'
)
BEGIN
    ALTER TABLE [Products] ADD [PackageLengthCm] decimal(18,2) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024142218_AddConfirmedPackagingSpecs'
)
BEGIN
    ALTER TABLE [Products] ADD [PackageWidthCm] decimal(18,2) NULL;
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024142218_AddConfirmedPackagingSpecs'
)
BEGIN
    ALTER TABLE [Products] ADD [PackagingSpecsConfirmed] bit NOT NULL DEFAULT CAST(0 AS bit);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024142218_AddConfirmedPackagingSpecs'
)
BEGIN
    EXEC(N'ALTER TABLE [Products] ADD CONSTRAINT [CK_Product_ConfirmedPackaging] CHECK (PackagingSpecsConfirmed = 0 OR (ShippingWeightGrams IS NOT NULL AND ShippingWeightGrams > 0 AND PackageLengthCm IS NOT NULL AND PackageLengthCm > 0 AND PackageWidthCm IS NOT NULL AND PackageWidthCm > 0 AND PackageHeightCm IS NOT NULL AND PackageHeightCm > 0))');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024142218_AddConfirmedPackagingSpecs'
)
BEGIN
    EXEC(N'ALTER TABLE [Products] ADD CONSTRAINT [CK_Product_PackageDimensions] CHECK ((PackageLengthCm IS NULL OR PackageLengthCm > 0) AND (PackageWidthCm IS NULL OR PackageWidthCm > 0) AND (PackageHeightCm IS NULL OR PackageHeightCm > 0))');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024142218_AddConfirmedPackagingSpecs'
)
BEGIN
    EXEC(N'ALTER TABLE [Products] ADD CONSTRAINT [CK_Product_ShippingWeight] CHECK (ShippingWeightGrams IS NULL OR ShippingWeightGrams > 0)');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261024142218_AddConfirmedPackagingSpecs'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261024142218_AddConfirmedPackagingSpecs', N'8.0.22');
END;
GO

COMMIT;
GO
