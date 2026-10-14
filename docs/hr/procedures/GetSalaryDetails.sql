USE [CoreERP]
GO
/****** Object:  UserDefinedFunction [dbo].[GetSalaryDetails]    Script Date: 14-Oct-2026 12:13:38 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- =============================================
-- Author:		<Author,,Name>
-- Create date: <Create Date,,>
-- Description:	<Description,,>
-- =============================================
ALTER FUNCTION [dbo].[GetSalaryDetails] 
(		
	@EmpId bigint=0,
	@SalaryMonth int,
	@SalaryYear int,
	@IsCurrentSalary int=0,-- for Initial Salary =0, CurrentSalary=1,Current Salary of Last day of month=2, Salary of Last day of month for HeadOffice=3
	@ContStartDate date
	
)
RETURNS @SalaryDetails TABLE 
(
	EmpId bigint,
	GrossSalary decimal(18,2),
	BasicSalary decimal(18,2),
	BasicSalaryOrg  decimal(18,2),
	HouseRent decimal(18,2),
	Medical decimal(18,2),
	Conveyance decimal(18,2),
	Food decimal(18,2),
	Other decimal(18,2),
	SpecialAllowance DECIMAL(18,2),
	TotalSalary DECIMAL(18,2)


	
)
AS
BEGIN
		--DECLARE @EmpId bigint=(SELECT empid FROM HumanResource_EmployeeBasic WHERE EmpCode='101499'),
		--@SalaryMonth int = 1,
		--@SalaryYear int=2026,
		--@IsCurrentSalary int=1,-- for Initial Salary =0, CurrentSalary=1
		--@ContStartDate date=NULL


	DECLARE
	@UnitId int,
	@DepartmentId int ,
	@SectionId int ,
	@WingId int,
	@TeamId int,
	@DesignationId int,
	@JoinDate date,
	@EmpTypeID int ,
	@EmpCategoryID int ,
	@PaymentModeId int  ,
	@CurrencyId int,
	@BankId int ,
	@BranchId int,
	@BankAccount nvarchar(50),
	@PayrollCompanyId int,
	@GrossSalary decimal(10,2)=0,
	@BasicSalary decimal(10,2)=0,
	@BasicSalaryOrg  decimal(10,2)=0,
	@HouseRent decimal(10,2)=0,
	@MedicalAllowance decimal(10,2)=0,
	@ConveyanceAllowance decimal(10,2)=0,
	@FoodingAllowance decimal(10,2)=0,
	@OthersAllowance decimal(10,2)=0,
	@SpecialAllowance DECIMAL(10,2),
	@SpecialAllowanceEffDate DATE,
	@KeyAllowance decimal(10,2)=0,
	@MiscAllowance decimal(10,2)=0,
	@GrossPayAmt decimal(10,2)=0,
    @BasicPayAmt decimal(10,2)=0,
    @AllowancePayAmt decimal(10,2)=0,
	@LayOffPayAmt decimal(10,2)=0,
	@GrossPayable decimal(10,2)=0,
	@AdvanceDed decimal(10,2)=0,
	@Adjustment decimal (10,2)=0,
	@AbsentDed decimal(10,2)=0,
	@LODed decimal(10,2)=0,
	@LWADed decimal(10,2)=0,
	@TaxDed decimal(10,2)=0,
	@LoanDed decimal(10,2)=0,
	@PFDed decimal(10,2)=0,
	@UnionSub decimal(10,2)=0,
	@Penalti decimal(10,2)=0,
	@LateDed decimal(10,2)=0,
    @StampFee decimal(10,2)=0,
	@NetPayment decimal(10,2)=0,
	@PerDayGross decimal(10,2)=0,
	@PerDayBasic decimal(10,2)=0,
	@PerDayConveyance decimal(10,2)=0,
	@PerDayLayOff decimal(10,2)=0,
	@AdvanceSalary decimal (10,2)=0,
	@SalaryStractureId int,
	@EffectDate datetime, 
	@Increment int = 0,
	@SortOrder int =0,
	@date datetime


	SET @date =CONVERT(DATETIME, STR(@SalaryMonth) + '/01/' + STR(@SalaryYear))


	SELECT @UnitId=BI.UnitID,
		   @DepartmentId=BI.DepartmentID,
		   @SectionId=BI.SectionID,
		   @WingId=WingID,
		   @TeamId=TeamID,
		   @DesignationId=BI.DesignationID,
		   @EmpTypeID=BI.EmpTypeID,
		   @EmpCategoryID=BI.EmpCategoryID,
		   @CurrencyId=BS.CurrencyId,
		   @BankId=BS.BankId,
		   @BranchId=BS.BranchId,
		   @BankAccount=BS.BankAccountNo,
		   @JoinDate=BI.JoiningDate,
		   @PaymentModeId=BS.PaymentModeId,
		   @PayrollCompanyId=BS.SalaryPaidCompany,
		   @GrossSalary=BS.CurrentSalary,

		   @GrossSalary=BS.GrossSalary,
		   @SalaryStractureId=BS.CalculationFormulaId


	FROM HumanResource_EmployeeBasic BI 
	INNER JOIN Payroll_Employee_BasicSalary BS ON BI.EmpID=BS.EmpID	
	WHERE BI.EmpID=@EmpId AND BI.EmpCategoryID <> 5

	IF @UnitId IN (1,27,45,111)
	BEGIN
		If @SalaryMonth=1
		begin
		SET @date=CONVERT(DATETIME, STR(12) + '/27/' + STR(@SalaryYear-1))
		end
		else
		begin
		SET @date=CONVERT(DATETIME, STR(@SalaryMonth-1) + '/27/' + STR(@SalaryYear))

		end
		
		IF @SalaryMonth=10 AND @SalaryYear=2022
		BEGIN
			SET @date =CONVERT(DATETIME, STR(@SalaryMonth) + '/01/' + STR(@SalaryYear))

		END
	END

	IF @IsCurrentSalary=2 AND @UnitId NOT IN (1,27,45,111)
		SET @date=EOMONTH(@date)
	
	IF @IsCurrentSalary=2 AND @UnitId IN (1,27,45,111)
	BEGIN
		SET @date=DATEADD(DAY,DATEPART(DAY,EOMONTH(@date))-1,@date)
	END

	IF @IsCurrentSalary=3 AND @UnitId IN (1,27,45,111)
	BEGIN
		SET @date=EOMONTH(DATEADD(DAY,DATEPART(DAY,EOMONTH(@date))-1,@date))
	END


	SET @Increment=(SELECT COUNT(EmpId) FROM Employee_IncrementInfo WHERE EmpId=@EmpId AND ISNULL(IsDeleted,0)=0) 
	SET @SortOrder=(SELECT Max(isnull(SortOrder,0)) FROM Employee_IncrementInfo WHERE EmpId=@EmpId AND ISNULL(IsDeleted,0)=0) 

	
	

	IF @Increment=0
	BEGIN
		SET @GrossSalary=@GrossSalary
		SET @EffectDate=@JoinDate
	END
	ELSE
	IF @increment=1
	BEGIN
		SET @EffectDate=(SELECT EffectDate  from Employee_IncrementInfo WHERE EmpId=@EmpId AND ISNULL(IsDeleted,0)=0)
		IF @date>=@EffectDate
		BEGIN	
			SET @EffectDate=(SELECT EffectDate  FROM Employee_IncrementInfo WHERE EmpId=@EmpId AND ISNULL(IsDeleted,0)=0)
			SET @GrossSalary=(SELECT NewGrossSalary  FROM Employee_IncrementInfo WHERE EmpId=@EmpId AND ISNULL(IsDeleted,0)=0)
		END
		ELSE
		BEGIN	
			SET @EffectDate=@JoinDate
			SET @GrossSalary=(SELECT MAX(PreviousSalary)  FROM Employee_IncrementInfo WHERE EmpId=@EmpId AND ISNULL(IsDeleted,0)=0)
		END
	END
	ELSE	
	IF @Increment>1
	BEGIN
	
		SET @EffectDate=(SELECT TOP 1 EffectDate FROM Employee_IncrementInfo WHERE EmpId=@EmpId AND EffectDate<=(CASE WHEN @ContStartDate IS NOT NULL THEN @ContStartDate ELSE @date END) AND ISNULL(IsDeleted,0)=0 ORDER BY SortOrder DESC,EffectDate DESC)
		

		IF (CASE WHEN @ContStartDate IS NOT NULL THEN @ContStartDate ELSE @date END)>=@EffectDate
		BEGIN

			IF EXISTS(Select TOP 1 EmpId From (select II.EmpId,II.EffectDate,II.IncrementId ,LAG(II.EffectDate) OVER (	PARTITION BY II.EmpId ORDER BY II.IncrementId	) PrevEffectDate From Employee_IncrementInfo II where II.IsDeleted<>1 AND II.EmpId=@EmpId )aa Where EmpId=@EmpId AND EffectDate < PrevEffectDate order by IncrementId desc)
			--IF EXISTS(Select TOP 1 * From (select * ,LAG(EffectDate) OVER (	PARTITION BY EmpId ORDER BY IncrementId	) PrevEffectDate From Employee_IncrementInfo where IsDeleted<>1 )aa Where EmpId=@EmpId AND EffectDate < PrevEffectDate order by IncrementId desc)
			BEGIN
				SET @EffectDate=(SELECT TOP 1 EffectDate  FROM Employee_IncrementInfo WHERE EmpId=@EmpId and EffectDate<=(CASE WHEN @ContStartDate IS NOT NULL THEN @ContStartDate ELSE @date END) AND ISNULL(IsDeleted,0)=0 ORDER BY SortOrder DESC,EffectDate desc)
			END
			ELSE
			BEGIN

				SET @EffectDate=(SELECT TOP 1 EffectDate  FROM Employee_IncrementInfo WHERE EmpId=@EmpId and EffectDate<=(CASE WHEN @ContStartDate IS NOT NULL THEN @ContStartDate ELSE @date END) AND ISNULL(IsDeleted,0)=0 ORDER BY EffectDate desc)
			END	

			If(@EmpId=1371)	
			BEGIN
				SET @EffectDate=(SELECT TOP 1 EffectDate  FROM Employee_IncrementInfo WHERE EmpId=@EmpId and EffectDate<=(CASE WHEN @ContStartDate IS NOT NULL THEN @ContStartDate ELSE @date END) AND ISNULL(IsDeleted,0)=0 ORDER BY EffectDate desc)
			END	

			IF(@UnitId IN (66,67,68))	--// for emergency support
			BEGIN

				SET @EffectDate=(SELECT TOP 1 EffectDate  FROM Employee_IncrementInfo WHERE EmpId=@EmpId and EffectDate<=(CASE WHEN @ContStartDate IS NOT NULL THEN @ContStartDate ELSE @date END) AND ISNULL(IsDeleted,0)=0 ORDER BY SortOrder DESC, EffectDate desc)
			END	
			
			SET @GrossSalary=(SELECT MAX(NewGrossSalary)  FROM Employee_IncrementInfo WHERE EmpId=@EmpId AND ISNULL(IsDeleted,0)=0 and EffectDate=@EffectDate )
			
		END
		ELSE
		BEGIN
			
			SET @EffectDate=@JoinDate
			SET @GrossSalary=(SELECT MIN(PreviousSalary)  FROM Employee_IncrementInfo WHERE EmpId=@EmpId AND ISNULL(IsDeleted,0)=0)
		END
	END			

	

	if(@SortOrder>0)
	BEGIN
		SET @GrossSalary=ISNULL((SELECT MAX(NewGrossSalary)  FROM Employee_IncrementInfo 
		WHERE ISNULL(SortOrder,0)=@SortOrder AND EmpId=@EmpId AND ISNULL(IsDeleted,0)=0 AND EffectDate=@EffectDate),@GrossSalary
		)
	END


	
	SELECT TOP 1 @SpecialAllowance=NewSpAllow,@SpecialAllowanceEffDate=EffectDate
	FROM Payroll_Employee_Special_Allowance WHERE EmpId=@EmpId AND EffectDate<=ISNULL(@ContStartDate,@date)
	AND IsDeleted<>1 ORDER BY EffectDate DESC,NewSpAllow DESC



	SET @SpecialAllowance=ISNULL(@SpecialAllowance,0)
	
	--//FOR SPECIAL ALLOWANCE ADJUSTMENT PROBLEM

	IF @UnitId IN (1,27,45) AND @EffectDate>@SpecialAllowanceEffDate AND GETDATE()>'2026-01-01' AND @EmpId NOT IN (79117,333127)
	BEGIN
		SET @GrossSalary=@GrossSalary-@SpecialAllowance
	END

	DECLARE @ConversionRate DECIMAL(6,2)

	SELECT TOP 1 @ConversionRate=ConversionRate FROM Payroll_CurrencyConversion WHERE CurrencyId=@CurrencyId 
		AND SalaryMonth<=@SalaryMonth AND SalaryYear<=@SalaryYear ORDER BY SalaryYear DESC, SalaryMonth DESC

	SET @GrossSalary = @GrossSalary * ISNULL(@ConversionRate,1)

	--SELECT 'T',@GrossSalary,@SortOrder,@CurrencyId,@ConversionRate

	IF @SalaryStractureId IN (2) --//Management Old 60%
	BEGIN
		IF @date>='2024-02-27' AND @date<'2024-03-27'
		BEGIN

			SET @ConveyanceAllowance=2500.00
			SET @BasicSalary=ROUND(((@GrossSalary)*.5),2)
			SET @BasicSalaryOrg=@BasicSalary
			SET @HouseRent=ROUND((@BasicSalary*.5),2)
			IF @HouseRent >= 25000.00
			BEGIN	
				SET @HouseRent=25000.00
			END
			SET @MedicalAllowance=ROUND((@BasicSalary*.1),2)
			IF @MedicalAllowance >= 10000.00
			BEGIN	
				SET @MedicalAllowance=10000.00
			END
			SET @OthersAllowance=@GrossSalary-(@BasicSalary+@HouseRent+@MedicalAllowance+@ConveyanceAllowance)		
		END
		ELSE IF @JoinDate>='2024-03-01'
		BEGIN
			
			SET @ConveyanceAllowance=2500.00
			SET @BasicSalary=ROUND(((@GrossSalary)*.5),2)
			SET @BasicSalaryOrg=@BasicSalary
			SET @HouseRent=ROUND((@BasicSalary*.5),2)
			IF @HouseRent >= 25000.00
			BEGIN	
				SET @HouseRent=25000.00
			END
			SET @MedicalAllowance=ROUND((@BasicSalary*.1),2)
			IF @MedicalAllowance >= 10000.00
			BEGIN	
				SET @MedicalAllowance=10000.00
			END
			SET @OthersAllowance=@GrossSalary-(@BasicSalary+@HouseRent+@MedicalAllowance+@ConveyanceAllowance)		
		END
		ELSE 
		BEGIN
			SET @ConveyanceAllowance=2500.00
			SET @BasicSalary=ROUND(((@GrossSalary-@ConveyanceAllowance)*.6),2)
			SET @BasicSalaryOrg=ROUND(((@GrossSalary-500)*.6),2)
			SET @HouseRent=ROUND((@BasicSalary*.5),2)
			IF @HouseRent >= 25000.00
			BEGIN	
				SET @HouseRent=25000.00
			END
			SET @MedicalAllowance=ROUND((@BasicSalary*.1),2)
			IF @MedicalAllowance >= 10000.00
			BEGIN	
				SET @MedicalAllowance=10000.00
			END
			SET @OthersAllowance=@GrossSalary-(@BasicSalary+@HouseRent+@MedicalAllowance+@ConveyanceAllowance)
		END
	END


	IF @SalaryStractureId IN (9) --//Management New 50%
	BEGIN

		SET @ConveyanceAllowance=2500.00
		SET @BasicSalary=ROUND(((@GrossSalary)*.5),2)
		SET @BasicSalaryOrg=@BasicSalary
		SET @HouseRent=ROUND((@BasicSalary*.5),2)
		IF @HouseRent >= 25000.00
		BEGIN	
			SET @HouseRent=25000.00
		END
		SET @MedicalAllowance=ROUND((@BasicSalary*.1),2)
		IF @MedicalAllowance >= 10000.00
		BEGIN	
			SET @MedicalAllowance=10000.00
		END
		SET @OthersAllowance=@GrossSalary-(@BasicSalary+@HouseRent+@MedicalAllowance+@ConveyanceAllowance)		
	END

	IF @SalaryStractureId IN (11) --//Management C&F
	BEGIN
		IF @JoinDate<'2024-03-01'
		BEGIN
			SET @ConveyanceAllowance=0
			SET @BasicSalary=ROUND(((@GrossSalary-500)*.6),2)
			SET @BasicSalaryOrg=@BasicSalary
			SET @HouseRent=ROUND(((@GrossSalary-500)*.4),2)
			SET @MedicalAllowance=500
			SET @OthersAllowance=@GrossSalary-(@BasicSalary+@HouseRent+@MedicalAllowance+@ConveyanceAllowance)
		END
		ELSE IF @JoinDate>='2024-03-01'
		BEGIN
			SET @ConveyanceAllowance=0
			SET @BasicSalary=ROUND(((@GrossSalary)*.5),2)
			SET @BasicSalaryOrg=@BasicSalary
			SET @HouseRent=ROUND(((@GrossSalary)*.5),2)
			SET @MedicalAllowance=500
			SET @OthersAllowance=@GrossSalary-(@BasicSalary+@HouseRent+@MedicalAllowance+@ConveyanceAllowance)
		END
	END


	IF @SalaryStractureId IN (10) --//Palstic Gazette
	BEGIN		
		SET @BasicSalary=ROUND(((@GrossSalary-1000)/1.4),2)
		SET @BasicSalaryOrg=@BasicSalary
		SET @HouseRent=ROUND((@BasicSalary*.4),2)		
		SET @FoodingAllowance=200
		SET @MedicalAllowance=500
		SET @ConveyanceAllowance=300	
	END


	IF @SalaryStractureId IN (1,5,6,12) --//Non Management GMT
	BEGIN
		IF @date <'2023-12-01'
		BEGIN
			SET @BasicSalary=ROUND((@GrossSalary-(1850))/1.5,2)	
			SET @BasicSalaryOrg=@BasicSalary
			SET @HouseRent=ROUND((@BasicSalary*50.00)/100.00,2)
			SET @MedicalAllowance=600
			SET @ConveyanceAllowance=350
			SET @FoodingAllowance=900	
		END
		ELSE
		BEGIN --//Non Management GMT New 2023
			SET @MedicalAllowance=750
			SET @ConveyanceAllowance=450
			SET @FoodingAllowance=1250	
			SET @BasicSalary=ROUND((@GrossSalary-(2450))/1.5,2)	
			SET @BasicSalaryOrg=@BasicSalary
			SET @HouseRent=ROUND((@BasicSalary*50.00)/100.00,2)
		END
	END

	IF @SalaryStractureId IN (3) --//Non Management Textile
	BEGIN
		IF @date<'2024-03-01'
		BEGIN
			SET @BasicSalary=ROUND((@GrossSalary-(850))/1.35,2)	
			SET @BasicSalaryOrg=@BasicSalary
			SET @HouseRent=ROUND((@BasicSalary*35.00)/100.00,2)
			SET @MedicalAllowance=550
			SET @ConveyanceAllowance=300
			SET @FoodingAllowance=0
		END
		ELSE
		BEGIN
			SET @BasicSalary=ROUND((@GrossSalary-2000)/1.55,2)	
			SET @BasicSalaryOrg=@BasicSalary
			SET @HouseRent=ROUND((@BasicSalary*.55),2)
			SET @MedicalAllowance=750
			SET @ConveyanceAllowance=400
			SET @FoodingAllowance=850
		END
	END
	IF @SalaryStractureId IN (7) --//Management Textile Factory
	BEGIN
		IF @date>='2024-02-27'  AND @date<'2024-03-27'
		BEGIN
			SET @ConveyanceAllowance=2500.00
			SET @BasicSalary=ROUND(((@GrossSalary)*.5),2)
			SET @BasicSalaryOrg=@BasicSalary
			SET @HouseRent=ROUND((@BasicSalary*.5),2)
			IF @HouseRent >= 25000.00
			BEGIN	
				SET @HouseRent=25000.00
			END
			SET @MedicalAllowance=ROUND((@BasicSalary*.1),2)
			IF @MedicalAllowance >= 10000.00
			BEGIN	
				SET @MedicalAllowance=10000.00
			END
			SET @OthersAllowance=@GrossSalary-(@BasicSalary+@HouseRent+@MedicalAllowance+@ConveyanceAllowance)		
		END
		ELSE IF @JoinDate>='2024-03-01'
		BEGIN
			SET @ConveyanceAllowance=2500.00
			SET @BasicSalary=ROUND(((@GrossSalary)*.5),2)
			SET @BasicSalaryOrg=@BasicSalary
			SET @HouseRent=ROUND((@BasicSalary*.5),2)
			IF @HouseRent >= 25000.00
			BEGIN	
				SET @HouseRent=25000.00
			END
			SET @MedicalAllowance=ROUND((@BasicSalary*.1),2)
			IF @MedicalAllowance >= 10000.00
			BEGIN	
				SET @MedicalAllowance=10000.00
			END
			SET @OthersAllowance=@GrossSalary-(@BasicSalary+@HouseRent+@MedicalAllowance+@ConveyanceAllowance)		
		END
		ELSE
		BEGIN
			SET @BasicSalary=ROUND((@GrossSalary-500)*0.60,2)	
			SET @HouseRent=ROUND((@BasicSalary)*0.50,0)
			SET @BasicSalaryOrg=ROUND(((@GrossSalary-500)*.6),2)
			SET @MedicalAllowance=500
			SET @ConveyanceAllowance=@GrossSalary-(@BasicSalary+@HouseRent+@MedicalAllowance)
			SET @FoodingAllowance=0
		END
		
	END

	IF @SalaryStractureId IN (4) --//Management Factory
	IF @date>='2024-02-27'  AND @date<'2024-03-27'
	BEGIN
	
		SET @ConveyanceAllowance=2500.00
		SET @BasicSalary=ROUND(((@GrossSalary)*.5),2)
		SET @BasicSalaryOrg=@BasicSalary
		SET @HouseRent=ROUND((@BasicSalary*.5),2)
		IF @HouseRent >= 25000.00
		BEGIN	
			SET @HouseRent=25000.00
		END
		SET @MedicalAllowance=ROUND((@BasicSalary*.1),2)
		IF @MedicalAllowance >= 10000.00
		BEGIN	
			SET @MedicalAllowance=10000.00
		END
		SET @OthersAllowance=@GrossSalary-(@BasicSalary+@HouseRent+@MedicalAllowance+@ConveyanceAllowance)		
	END
	ELSE IF @JoinDate>='2024-03-01'
		BEGIN
			SET @ConveyanceAllowance=2500.00
			SET @BasicSalary=ROUND(((@GrossSalary)*.5),2)
			SET @BasicSalaryOrg=@BasicSalary
			SET @HouseRent=ROUND((@BasicSalary*.5),2)
			IF @HouseRent >= 25000.00
			BEGIN	
				SET @HouseRent=25000.00
			END
			SET @MedicalAllowance=ROUND((@BasicSalary*.1),2)
			IF @MedicalAllowance >= 10000.00
			BEGIN	
				SET @MedicalAllowance=10000.00
			END
			SET @OthersAllowance=@GrossSalary-(@BasicSalary+@HouseRent+@MedicalAllowance+@ConveyanceAllowance)		
		END
	ELSE
	BEGIN
	
		SET @BasicSalary=ROUND((@GrossSalary-500)*0.60,2)	
		SET @BasicSalaryOrg=ROUND(((@GrossSalary-500)*.6),2)
		SET @HouseRent=ROUND((@GrossSalary-500)*0.40,2)
		SET @MedicalAllowance=500
		SET @ConveyanceAllowance=0
		SET @FoodingAllowance=0
	END


	IF @SalaryStractureId IN (13) --//Management 60%
	BEGIN
		SET @BasicSalary=ROUND((@GrossSalary-500)*0.60,2)	
		SET @BasicSalaryOrg=ROUND(((@GrossSalary-500)*.6),2)
		SET @HouseRent=ROUND((@GrossSalary-500)*0.40,2)
		SET @MedicalAllowance=500
		SET @ConveyanceAllowance=0
		SET @FoodingAllowance=0
	END


	--IF @EmpId=333127
	--BEGIN
	--	SET @BasicSalary=0
	--	SET @HouseRent=0
	--	SET @MedicalAllowance=0
	--	SET @ConveyanceAllowance=0
	--	SET @FoodingAllowance=0
	--	SET @OthersAllowance=10000
	--END


	INSERT INTO @SalaryDetails
	Select @EmpId,GrossSalary=@GrossSalary,
	BasicSalary=@BasicSalary,
	BasicSalaryOrg=@BasicSalaryOrg,
	HouseRent=@HouseRent,
	Medical=@MedicalAllowance,
	Conveyance=@ConveyanceAllowance,
	Food=@FoodingAllowance,
	Other=@OthersAllowance,
	SpecialAllowance=@SpecialAllowance,
	TotalSalary=@GrossSalary+@SpecialAllowance

	--select SalaryHeadId=1,Amount=@BasicSalary,EffectDate=@JoinDate
	--union all 
	--Select 2,Amount=@HouseRent,EffectDate=@JoinDate
	--union all 
	--Select 3,Amount=@ConveyanceAllowance,EffectDate=@JoinDate
	--union all 
	--Select 4,Amount=@FoodingAllowance,EffectDate=@JoinDate
	--union all 
	--Select 5,Amount=@OthersAllowance,EffectDate=@JoinDate
	--union all 
	--Select 6,Amount=@MedicalAllowance,EffectDate=@JoinDate
	RETURN 
END













