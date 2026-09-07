CREATE TABLE [dbo].[Customers] (
    [Id] INT IDENTITY(1,1) NOT NULL,
    [Name] NVARCHAR(200) NOT NULL,
    [Cpf] CHAR(11) NULL,
    [Email] NVARCHAR(320) NULL,
    [CardNumber] VARCHAR(19) NULL,
    CONSTRAINT [PK_Customers] PRIMARY KEY CLUSTERED ([Id])
);
GO
CREATE TABLE Orders (
    Id int not null,
    CustomerId int not null,
    Total decimal(18,2) not null
);
GO
CREATE TRIGGER trg_Orders_Audit ON Orders AFTER INSERT AS BEGIN SET NOCOUNT ON; END
GO
CREATE PROCEDURE dbo.usp_Bill @id INT AS
BEGIN
    DECLARE cur CURSOR FOR SELECT Id FROM Orders;
END
