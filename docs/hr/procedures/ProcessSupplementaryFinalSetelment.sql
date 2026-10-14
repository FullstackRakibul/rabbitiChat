USE [CoreERP]
GO
/****** Object:  StoredProcedure [dbo].[ProcessSupplementaryFinalSetelment]    Script Date: 14-Oct-2026 12:16:20 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO


ALTER Procedure [dbo].[ProcessSupplementaryFinalSetelment]	@EmpId int,@SalaryMonth int,@SalaryYear int,@p_UserId int=0 AS
BEGIN
	--DECLARE @EmpId int=(SELECT EmpID FROM HumanResource_EmployeeBasic WHERE EmpCode='115978'),@SalaryMonth int=08,@SalaryYear int=2026,@p_UserId int=0

	DECLARE
	@UnitId int ,
	@DepartmentId int ,
	@SectionId int ,
	@WingId int,
	@TeamId int,
	@DesignationId int,
	@JoinDate date,
	@PaymentModeId bit  ,
	@EmpTypeID int,
	@CurrencyId int,
	@BankId int ,
	@BranchId int,
	@BankAccount nvarchar(50),
	@PayrollCompanyId int,	
	@TotalDays int,
	@MOD int,
	@Present int  ,
	@Absent int  ,
	@Late int  ,
	@EXL int ,
	@LWP int  ,
	@LWA int,
	@EL int  ,
	@CL int  ,
	@ML int  ,
	@SPL int,
	@EE int,
	@WO int,
	@H int,
	@NOP int,
	@LO int,
	@UL int,
	@COL int,
	@DS int,
	@GrossDays int=0  ,
	@BasicDays int=0  ,
	@AllowanceDays int=0  ,
	@LayOffDays int=0,
	@Adjustment decimal (10,2)=0,
	@GrossSalary decimal(10,2)=0,
	@BasicSalary decimal(10,2)=0,
	@HouseRent decimal(10,2)=0,
	@MedicalAllowance decimal(10,2)=0,
	@ConveyanceAllowance decimal(10,2)=0,
	@FoodingAllowance decimal(10,2)=0,
	@OthersAllowance decimal(10,2)=0,
	@KeyAllowance decimal(10,2)=0,
	@GrossPayAmt decimal(10,2)=0,
    @BasicPayAmt decimal(10,2)=0,
    @AllowancePayAmt decimal(10,2)=0,
	@LayOffPayAmt decimal(10,2)=0,
	@GrossPayable decimal(10,2)=0,
	@AdvanceDed decimal(10,2)=0,
	@AdvanceAdd decimal(10,2)=0,
	@AbsentDed decimal(10,2)=0,
	@LWADed decimal(10,2)=0,
	@TaxDed decimal(10,2)=0,
	@LoanDed decimal(10,2)=0,
	@LODed decimal(10,2)=0,
	@Penalti decimal(10,2)=0,
	@LateDed decimal(10,2)=0,
    @StampFee decimal(10,2)=0,
	@SpecialAllowanceRate decimal(10,2),
	@SpecialAllowance decimal(10,2),
	@PerDaySpecialAllowance DECIMAL(10,2),
	@NetPayment decimal(10,2)=0,
	@TotalPayment decimal(10,2)=0,
	@PerDayGross decimal(10,2)=0,
	@PerDayBasic decimal(10,2)=0,
	@PerDayConveyance decimal(10,2)=0,
	@PerDayLayOff decimal(10,2)=0,
	@AdvanceSalary decimal (10,2)=0,
	@OverPay decimal (10,2)=0,

	@MobileAllowance decimal(10,2)=0,
	@TiffinRate decimal(10,2)=0,
    @TiffinDay int=0,
    @TiffinAmount decimal(10,2)=0,
	@IfterRate decimal(10,2)=0,
    @IfterDay int=0,
    @IfterAmount decimal(10,2)=0,
    @HolidayRate decimal(10,2)=0,
    @HolidayDay int =0,
    @HolidayAmount decimal(10,2)=0,
	@FestivalHolidaysDay int =0,
    @FestivalHolidaysAmount decimal(10,2)=0,
    @FestivalHolidaysRate decimal(10,2)=0,
    @NightRate decimal(10,2)=0,
    @NightDay int=0,
    @NightAmount decimal(10,2)=0,
	@NightIncentiveRate decimal(10,2)=0,
	@NightIncentiveDay int=0,
	@NightIncentiveAmount decimal(10,2)=0,
	@LunchRate decimal(10,2)=0,
    @LunchDay int=0,
    @LunchAmount decimal(10,2)=0,

	@OTRate decimal(10,2)=0,
	@IsFixed bit, --//For OT Holder 
	@FixedOTRate decimal(10,2)=0,
	@DayAllowRate decimal(10,2)=0,
	@DayAllowAmount decimal(10,2)=0,
	--@OTHour int=0,
	@OTHour decimal(10,2)=0,
	@OTMin INT,
	@OTAmount decimal(10,2)=0,
	@AttBonusRate decimal (10,2)=0,
	@AttBonus decimal (10,2)=0,

	@StopSupplementary bit=0,
	@SeparationStatus bit=0,
	@IsPFHolder bit=0,
	@PFDed decimal(10,2)=0,
	@SalaryStractureId int,
	@SL int=0,
	@IsApproved bit,
	@Increment int = 0,
	@EffectDate datetime,
	@NewJoin bit=0,
	@SalaryMood varchar(1)='I',
	@FromDate date,
	@TillDate date,
	@date datetime,
	@SeparationType INT = 0,
	@SeparationEffectDate DATE

	IF EXISTS (SELECT * FROM ProcessLock WHERE LMonth=@SalaryMonth AND LYear=@SalaryYear AND PayrollTypeID=3 AND LockDate < CAST(GETDATE() AS date))
	BEGIN
		GOTO TheEnd;
	END

	SET @date =CONVERT(DATETIME, STR(@SalaryMonth) + '/01/' + STR(@SalaryYear))
	if(@SalaryMonth=1)
	begin
	SET @FromDate=CONVERT(DATETIME,  '12/27/' + STR(@SalaryYear-1))
	end
	else
	begin
	SET @FromDate=CONVERT(DATETIME, STR(@SalaryMonth-1) + '/27/' + STR(@SalaryYear))
	end
	
	SET @TillDate=CONVERT(DATETIME, STR(@SalaryMonth) + '/26/' + STR(@SalaryYear))		

	SET @MOD= DAY(DATEADD (m, 1, DATEADD (d, 1 - DAY(@date), @date)) - 1) 

	DELETE FROM Payroll_MonthlySalarySupplementary WHERE EmpId=@EmpId AND SalaryMonth=@SalaryMonth AND SalaryYear=@SalaryYear AND CAST(ActionTime AS date)=CAST(GETDATE() AS date) AND Unpaid=1

	DECLARE @GetSalaryLastDate DATE=EOMONTH(DATEFROMPARTS(@SalaryYear,@SalaryMonth,01))

	SELECT 	@SeparationStatus=(IIF(A.EmpStatusID<>1,1,0))
	, 		@SeparationType=A.EmpStatusID
	, 		@SeparationEffectDate=EffectDate
	,		@UnitId=A.UnitID
	FROM 		HumanResource_EmployeeBasic		 	A	
	INNER JOIN 	HumanResource_EmployeeSeparation 	B	 ON B.EmpID=A.EmpID AND B.IsActive=1
	WHERE A.EmpID=@EmpId

	DECLARE @SeparationCheckDate DATE=DATEADD(DAY,-1,@SeparationEffectDate)

	IF @UnitId IN (1,27,45,111) AND ((DAY(@SeparationEffectDate) BETWEEN 27 AND 31) OR DAY(@SeparationEffectDate)=1)
		AND MONTH(DATEADD(DAY,5,@SeparationEffectDate))=@SalaryMonth AND YEAR(DATEADD(DAY,5,@SeparationEffectDate))=@SalaryYear
	BEGIN
		SET @SeparationStatus=@SeparationStatus
	END
	ELSE IF (MONTH(@SeparationCheckDate)<>@SalaryMonth OR YEAR(@SeparationCheckDate)<>@SalaryYear)
	BEGIN
		SET @SeparationStatus=0
	END


	SET @IsApproved=(SELECT IsApproved FROM HumanResource_EmployeeBasic WHERE EmpID=@EmpId)
	--For Salary Stop
	--StopTypeId=1(Monthly), StopPayrollTypeId=1(Salary Stop),StopTypeId=3(Untill Open)
	IF EXISTS(SELECT * FROM Payroll_MonthlyPayrollStop WHERE EmpID=@EmpId and MonthFrom=@SalaryMonth AND YearFrom=@SalaryYear AND StopTypeId=1 and StopPayrollTypeId=3 and IsOpen=0) 
	BEGIN

	 SET @StopSupplementary=1
	END

	IF EXISTS(SELECT * FROM Payroll_MonthlyPayrollStop WHERE EmpID=@EmpId and StopTypeId=3 and StopPayrollTypeId=3 and IsOpen=0 AND  EffectDate <= @date) 
	BEGIN
	 SET @StopSupplementary=1
	END


	IF @SeparationStatus=1 AND @StopSupplementary=0 AND @IsApproved=1
	BEGIN--Main Start 


	SELECT @JoinDate=JoiningDate FROM HumanResource_EmployeeBasic WHERE EmpID=@EmpId

		SELECT @UnitId=BI.UnitID,
			   @DepartmentId=BI.DepartmentID,
			   @SectionId=BI.SectionID,
			   @WingId=WingID,
			   @TeamId=TeamID,
			   @DesignationId=BI.DesignationID,
			   @EmpTypeID=BI.EmpTypeID,
			   @CurrencyId=BS.CurrencyId,
			   @BankId=BS.BankId,
			   @BranchId=BS.BranchId,
			   @BankAccount=BS.BankAccountNo,
			   @JoinDate=BI.JoiningDate,
			   @PaymentModeId=BS.PaymentModeId,
			   @PayrollCompanyId=BS.SalaryPaidCompany,
			   @GrossSalary=BS.CurrentSalary,
			   @SalaryStractureId=BS.CalculationFormulaId,
			   @IsFixed=IsFixed, 
			   @KeyAllowance=isnull(A.KeyAllow,0),
			   @MobileAllowance=isnull(A.MobileAllow,0),
			   @TiffinRate=isnull(A.TiffinAllow,0),
			   @IfterRate=isnull(A.IftarAllow,0),
			   @HolidayRate=isnull(A.HolidayAllow,0),
			   @NightRate=isnull(A.NightAllow,0),
			   @NightIncentiveRate=isnull(A.NightInsentive,0),
		       @LunchRate=isnull(A.LunchAllow,0),
			   @AttBonusRate=isnull(A.AttBonus,0),
			   @FixedOTRate=isnull(A.OTRate,0),
			   @DayAllowRate=isnull(A.DayAllow,0),
			   @FestivalHolidaysRate = ISNULL(A.FastivalAllow,0)
			   

	FROM HumanResource_EmployeeBasic BI 
	LEFT JOIN Payroll_Employee_BasicSalary BS ON BI.EmpID=BS.EmpID	
	LEFT JOIN dbo.GetAllowanceOrBill(@EmpId,IIF(@date<@JoinDate,@JoinDate,@date),'S') A ON BI.EmpID=A.empId
	WHERE BI.EmpID=@EmpId

	

	IF @UnitId IN (1,27,45,111)
	BEGIN
		SET @MOD= DATEDIFF(DAY,@FromDate,@TillDate)+1
		IF @SalaryMonth=10 AND @SalaryYear=2022
		BEGIN
			SET @FromDate=@date
		END
	END
	ELSE
	BEGIN
		SET @FromDate=@date
		SET @TillDate=EOMONTH(@FROMDATE)
		SET @MOD= DAY(DATEADD (m, 1, DATEADD (d, 1 - DAY(@date), @date)) - 1) 
	END

	
	IF @JoinDate > @FromDate
	BEGIN
		SET @NewJoin=1
		SET @SalaryMood='N'
	END


	SET @Increment=(SELECT COUNT(EmpId) FROM Employee_IncrementInfo WHERE EmpId=@EmpId AND ISNULL(IsDeleted,0)=0)
				
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
		SET @EffectDate=(SELECT MAX(EffectDate) FROM Employee_IncrementInfo WHERE EmpId=@EmpId AND EffectDate<=@date AND ISNULL(IsDeleted,0)=0)
		IF @date>=@EffectDate
		BEGIN
			SET @EffectDate=(SELECT TOP 1 EffectDate  FROM Employee_IncrementInfo WHERE EmpId=@EmpId and EffectDate<=@date AND ISNULL(IsDeleted,0)=0 ORDER BY EffectDate desc)
			SET @GrossSalary=(SELECT MAX(NewGrossSalary)  FROM Employee_IncrementInfo WHERE EmpId=@EmpId AND ISNULL(IsDeleted,0)=0 and EffectDate=@EffectDate )
		END
		ELSE
		BEGIN
			SET @EffectDate=@JoinDate
			SET @GrossSalary=(SELECT MIN(PreviousSalary)  FROM Employee_IncrementInfo WHERE EmpId=@EmpId AND ISNULL(IsDeleted,0)=0)
		END
	END

	SELECT @GrossSalary=@GrossSalary*ConversionRate FROM Payroll_CurrencyConversion WHERE CurrencyId=@CurrencyId AND SalaryMonth=@SalaryMonth AND SalaryYear=@SalaryYear
	
	SELECT TOP 1 @TaxDed=TaxAmountEmp FROM Payroll_EmployeeTaxation WHERE EffectiveDate <= DATEFROMPARTS(@SalaryYear, @SalaryMonth,1) AND IsLocked=0 AND EmpId=@EmpId ORDER BY EffectiveDate desc

	--SELECT @PFDed=PFAmountEmp FROM Payroll_EmployeePFDeduction WHERE SalaryMonth = @SalaryMonth AND SalaryYear = @SalaryYear AND IsLocked=0  AND EmpId=@EmpId

	SELECT @AdvanceDed= ISNULL(SUM(Amount), 0) FROM Payroll_EmployeePayrollAdjustment WHERE SalaryMonth = @SalaryMonth AND SalaryYear = @SalaryYear AND IsLocked=0  AND EmpId=@EmpId AND AdjustmentTypeId=1 AND ISNULL(IsDeleted,0)=0


	SELECT @Adjustment=ISNULL(Sum(Amount),0) FROM Payroll_EmployeePayrollAdjustment WHERE SalaryMonth = @SalaryMonth AND SalaryYear = @SalaryYear AND IsLocked=0 AND EmpId=@EmpId and AdjustmentTypeId=2 and PayrollTypeId=1 And ISNULL(IsDeleted,0)=0


	--SELECT @LoanDed=ISNULL(LoanAmount,0) FROM Payroll_EmployeeLoan  WHERE EmpID = @EmpId -- RAKIBUL


	SELECT @AdvanceAdd=Amount FROM Payroll_EmployeePayrollAdjustment WHERE SalaryMonth = @SalaryMonth AND SalaryYear = @SalaryYear AND IsLocked=0 AND EmpId=@EmpId AND AdjustmentTypeId=2 AND ISNULL(IsDeleted,0)=0

	SELECT @AdvanceSalary=ISNULL(NetPayment+OTAmount,0) FROM Payroll_MonthlySalaryAdvance where EmpId=@EmpId and SalaryMonth=@SalaryMonth and SalaryYear=@SalaryYear and UnPaid=0	

	IF EXISTS (SELECT * FROM Payroll_MonthlySalary WHERE EmpId =@EmpId AND SalaryMonth=@SalaryMonth AND SalaryYear=@SalaryYear)
	BEGIN
		SELECT @GrossSalary=GrossSalary FROM Payroll_MonthlySalary WHERE EmpId =@EmpId AND SalaryMonth=@SalaryMonth AND SalaryYear=@SalaryYear
	END
	
	-- SALARY WILL PROCESS TILL SEPARATION EFFECT DATE 
	SET @TillDate = IIF(@SeparationStatus = 1, @SeparationEffectDate, @TillDate)

	DECLARE @ContStartDate date
	SELECT @ContStartDate=max(ContractStartDate) FROM Employee_ContractInfo 
	WHERE isnull(IsDeleted,0)=0  and YEAR(ContractStartDate)=@SalaryYear AND MONTH(ContractStartDate)=@SalaryMonth AND EmpId=@EmpId
	GROUP BY EmpId

	
	
	SELECT @Present=ISNULL(SUM(CASE WHEN  AttStatus IN ('P','LTP','SD','TL') THEN 1 ELSE 0 END),0),
		   @Absent =ISNULL(SUM(CASE WHEN  AttStatus IN ('A') THEN 1 ELSE 0 END),0),
		   @Late =ISNULL(SUM(CASE WHEN  AttStatus IN ('LT') THEN 1 ELSE 0 END),0),
		   @EXL=ISNULL(SUM(CASE WHEN  AttStatus IN ('EXL') THEN 1 ELSE 0 END),0),
		   @LWP=ISNULL(SUM(CASE WHEN  AttStatus IN ('LWP') THEN 1 ELSE 0 END),0),
		   @LWA=ISNULL(SUM(CASE WHEN  AttStatus IN ('LWA') THEN 1 ELSE 0 END),0),
		   @CL=ISNULL(SUM(CASE WHEN  AttStatus IN ('CL') THEN 1 ELSE 0 END),0),
		   @ML=ISNULL(SUM(CASE WHEN  AttStatus IN ('SL','ML','ICL') THEN 1 ELSE 0 END),0),
		   @COL=ISNULL(SUM(CASE WHEN  AttStatus IN ('COL') THEN 1 ELSE 0 END),0),
		   @EL=ISNULL(SUM(CASE WHEN  AttStatus IN ('EL','AL') THEN 1 ELSE 0 END),0),
		   @SPL=ISNULL(SUM(CASE WHEN  AttStatus IN ('SPL') THEN 1 ELSE 0 END),0),
		   @WO=ISNULL(SUM(CASE WHEN  AttStatus IN ('WO') THEN 1 ELSE 0 END),0),
		   @H=ISNULL(SUM(CASE WHEN  AttStatus IN ('H') THEN 1 ELSE 0 END),0),
		   @EE=ISNULL(SUM(CASE WHEN  AttStatus IN ('EE') THEN 1 ELSE 0 END),0),
		   @NOP=ISNULL(SUM(CASE WHEN  AttStatus IN ('NOP') THEN 1 ELSE 0 END),0),
		   @LO=ISNULL(SUM(CASE WHEN  AttStatus IN ('LO') THEN 1 ELSE 0 END),0),
		   @UL=ISNULL(SUM(CASE WHEN  AttStatus IN ('UL') THEN 1 ELSE 0 END),0),
		   @DS=ISNULL(SUM(CASE WHEN  AttStatus IN ('DS') THEN 1 ELSE 0 END),0),

		   @TiffinDay=ISNULL(SUM(CASE WHEN IsTiffin=1 THEN 1 ELSE 0 END),0),
		   @HolidayDay=ISNULL(SUM(CASE WHEN IsWeekOff=1 THEN 1 ELSE 0 END),0),
		   @FestivalHolidaysDay=ISNULL(SUM(CASE WHEN IsHoliday=1 THEN 1 ELSE 0 END),0),
		   @NightDay=ISNULL(SUM(CASE WHEN IsNight=1 THEN 1 ELSE 0 END),0),
		   @NightIncentiveDay=ISNULL(SUM(CASE WHEN IsNightIncentive=1 THEN 1 ELSE 0 END),0),
		   @IfterDay=ISNULL(SUM(CASE WHEN IsIftar=1 THEN 1 ELSE 0 END),0),
		   @LunchDay=ISNULL(SUM(CASE WHEN IsLunch=1 THEN 1 ELSE 0 END),0),
		   @OTHour=ISNULL(FLOOR(SUM(OtHours)),0),
		   @OTMin=ISNULL(SUM((OtHours-FLOOR(OtHours))*100),0)
		   

	  FROM Attendance_DailyAttendance WHERE WorkDay BETWEEN @FromDate AND @TillDate AND EmpId=@EmpId 
	  AND WorkDay>=(case when @ContStartDate is not null then @ContStartDate else @FromDate end)
	  	

		IF @SalaryMonth=4 AND @SalaryYear=2020
		BEGIN
			SET @LO=@LO+@Absent
			SET @Present=@Present+@Late+@EXL+@NOP+@EE
			SET @Absent=0		
			SET @Late=0
			SET @EXL=0
			SET @NOP=0
			SET @EE=0
		END
		
		--IF @SalaryStractureId IN (2) --//Management
		--BEGIN
		--	SET @ConveyanceAllowance=2500.00
		--	SET @BasicSalary=ROUND(((@GrossSalary-@ConveyanceAllowance)*.6),2)
		--	SET @HouseRent=ROUND((@BasicSalary*.5),2)
		--	IF @HouseRent >= 25000.00
		--	BEGIN	
		--		SET @HouseRent=25000.00
		--	END
		--	SET @MedicalAllowance=ROUND((@BasicSalary*.1),2)
		--	IF @MedicalAllowance >= 10000.00
		--	BEGIN	
		--		SET @MedicalAllowance=10000.00
		--	END
		--	SET @OthersAllowance=@GrossSalary-(@BasicSalary+@HouseRent+@MedicalAllowance+@ConveyanceAllowance)		
		--END

		--IF @SalaryStractureId IN (1,6) --//Non Management GMT
		--BEGIN
		--	SET @BasicSalary=ROUND((@GrossSalary-(1850))/1.5,2)	
		--	SET @HouseRent=ROUND((@BasicSalary*50.00)/100.00,2)
		--	SET @MedicalAllowance=600
		--	SET @ConveyanceAllowance=350
		--	SET @FoodingAllowance=900	
		--END

		--IF @SalaryStractureId IN (3) --//Non Management Textile
		--BEGIN
		--	SET @BasicSalary=ROUND((@GrossSalary-(850))/1.35,2)	
		--	SET @HouseRent=ROUND((@BasicSalary*35.00)/100.00,2)
		--	SET @MedicalAllowance=550
		--	SET @ConveyanceAllowance=300
		--	SET @FoodingAllowance=0
		--END
		
			SELECT @GrossSalary=GrossSalary,@BasicSalary=BasicSalary,@HouseRent=HouseRent,@MedicalAllowance=Medical,@ConveyanceAllowance=Conveyance,@FoodingAllowance=Food,
			@OthersAllowance=Other,@SpecialAllowanceRate=SpecialAllowance
	  FROM [dbo].[GetSalaryDetails] (@EmpId,@SalaryMonth,@SalaryYear,1,@ContStartDate)

	 

		SET @TotalDays=@Present+@Absent+@Late+@LWP+@LWA+@CL+@EL+@ML+@WO+@H+@SPL+@EE+@EXL+@NOP+@LO+@UL+@COL+@DS

		
	
		SET @LWA=@LWA+@UL


		IF (@Present+@CL+@ML+@EL+@Late+@LWP+@LWA+@SPL+@H+@LO+@COL+@DS+@EE+@NOP+@Absent+@WO)>0
		BEGIN

			IF @UnitId IN (2,3,4,5,6,7,8,9,10,16,20,25,26,30,31,32,33,34,38,39,40,41,43,44,46,47,48,51,53,54,55,56,57,58,59,60,61,62,63,64,65,66,67,68,69,70,71,72,73,78,79,81,90,91,92,93,22,23,24,94,95,109,110,113,86,118,119,121)
				
				SET @GrossDays=@Present+@CL+@ML+@EL+@Late+@WO+@H+@SPL+@EE+@EXL+@NOP+@COL


			ELSE
				SET @GrossDays=@Present+@CL+@ML+@EL+@Late+@WO+@H+@SPL

				SET @BasicDays=0
			
			
			IF @UnitId NOT IN (1,27,45,12,111)
			BEGIN
				IF @UnitId=19
				BEGIN	
					IF @EmpTypeID = 1
					BEGIN
						SET @AllowanceDays=@LWP+@EE+@EXL+@NOP
					END
					ELSE
					BEGIN					
						IF @NewJoin=1
							SET @AllowanceDays=@LWP+@EE+@EXL+@NOP+@DS
						ELSE
							SET @AllowanceDays=@LWP+@Absent+@EE+@EXL+@NOP+@DS
					END
				END
				ELSE IF @UnitId IN (2,3,4,5,6,7,8,9,10,16,20,25,26,30,31,32,33,34,38,39,40,41,43,44,46,47,48,51,53,54,55,56,57,58,59,60,61,62,63,64,65,66,67,68,69,70,71,72,73,78,79,81,90,91,92,93,22,23,24 ,94,86, 95,109,110,113,118,119,121)
				BEGIN
					SET @AllowanceDays=@LWP+@Absent+@DS
					SET @Present=@Present+@EE+@EXL+@NOP
				END
				ELSE
				BEGIN
					SET @AllowanceDays=@LWP+@Absent+@EE+@EXL+@NOP+@DS	
					
					
				END
				IF @SalaryStractureId IN (5)
				BEGIN
					SET @AllowanceDays=0
				END
			END
			ELSE
			BEGIN
				SET @AllowanceDays=@LWP+@DS
			END				
			SET @LayOffDays=@LO
		END


		
		--late deduction start

		DECLARE @ELBalance int =0,@LateDedDay int=0,@LatePrmDay int=0

		IF @UnitId IN (1,27,45)
		BEGIN
			SET @LatePrmDay=2
		END
		ELSE IF @UnitId IN (19)
		BEGIN
			IF @EmpTypeID IN (1)
				SET @LatePrmDay=2
			ELSE
				SET @LatePrmDay=3
		END

		DECLARE @app_code bigint=0
		DECLARE @deduction_day int=0
		
		SELECT @ELBalance=ISNULL((LeaveAllocated-ISNULL(LeaveUsed,0)),0) FROM LeaveAllocation WHERE EmpId=@EmpId AND LeaveId=3 and leaveyear=@SalaryYear

		IF EXISTS (SELECT * FROM [LeaveDeduction] WHERE EmpId=@EmpId AND DYear=@SalaryYear AND DMonth=@SalaryMonth AND Remarks='EL Deduction For Late')
		BEGIN
			SELECT @ELBalance=@ELBalance+DLeave  FROM [LeaveDeduction] WHERE EmpId=@EmpId AND DYear=@SalaryYear AND DMonth=@SalaryMonth AND Remarks='EL Deduction For Late'
		END	
		
		DECLARE @ISLateSalaryDed bit=0;

		IF @Late > @LatePrmDay AND @LatePrmDay <> 0
		BEGIN--1

				SET @LateDedDay=@Late/3
				IF @ELBalance<>0
				BEGIN	
					IF @ELBalance>=@LateDedDay
					BEGIN
						IF EXISTS (SELECT * FROM [LeaveDeduction] WHERE EmpId=@EmpId AND DYear=@SalaryYear AND DMonth=@SalaryMonth AND Remarks='EL Deduction For Late')
						BEGIN							
							SELECT @app_code=DedID,@deduction_day=DLeave FROM [LeaveDeduction] WHERE EmpId=@EmpId AND DYear=@SalaryYear AND DMonth=@SalaryMonth AND Remarks='EL Deduction For Late' 

							EXEC sp_ManageLeaveDeduction 'Remove',@app_code,3,@EmpId,@SalaryMonth,@SalaryYear,@Late,@deduction_day,'EL Deduction For Late',100,0,@SalaryYear

							EXEC sp_ManageLeaveDeduction 'Add',0,3,@EmpId,@SalaryMonth,@SalaryYear,@Late,@LateDedDay,'EL Deduction For Late',100,0,@SalaryYear
						END
						ELSE
						BEGIN
							EXEC sp_ManageLeaveDeduction 'Add',0,3,@EmpId,@SalaryMonth,@SalaryYear,@Late,@LateDedDay,'EL Deduction For Late',100,0,@SalaryYear
						END
					END
					ELSE
					BEGIN	
					IF EXISTS (SELECT *  FROM [LeaveDeduction] WHERE EmpId=@EmpId AND dyear=@SalaryYear AND dmonth=@SalaryMonth AND remarks='EL Deduction For Late')
					BEGIN
						SELECT @app_code=DedID,@deduction_day=DLeave FROM [LeaveDeduction] WHERE EmpId=@EmpId AND dyear=@SalaryYear AND dmonth=@SalaryMonth AND remarks='EL Deduction For Late' 

						EXEC sp_ManageLeaveDeduction 'Remove',@app_code,3,@EmpId,@SalaryMonth,@SalaryYear,@Late,@deduction_day,'EL Deduction For Late',100,0,@SalaryYear
					END

					

						SET @ISLateSalaryDed=1	
						SET @AllowanceDays=@AllowanceDays+@LateDedDay
						SET @GrossDays=@GrossDays-@LateDedDay
					END
				END
				ELSE
				BEGIN
					IF EXISTS (SELECT *  FROM [LeaveDeduction] WHERE EmpId=@EmpId AND dyear=@SalaryYear AND dmonth=@SalaryMonth AND remarks='EL Deduction For Late')
					BEGIN
						SELECT @app_code=DedID,@deduction_day=DLeave FROM [LeaveDeduction] WHERE EmpId=@EmpId AND dyear=@SalaryYear AND dmonth=@SalaryMonth AND remarks='EL Deduction For Late' 

						EXEC sp_ManageLeaveDeduction 'Remove',@app_code,3,@EmpId,@SalaryMonth,@SalaryYear,@Late,@deduction_day,'EL Deduction For Late',100,0,@SalaryYear
					END
					SET @ISLateSalaryDed=1

					
					SET @AllowanceDays=@AllowanceDays+@LateDedDay
					SET @GrossDays=@GrossDays-@LateDedDay
				END			
		END--1		
		ELSE
		BEGIN
			IF EXISTS (SELECT *  FROM [LeaveDeduction] WHERE EmpId=@EmpId AND dyear=@SalaryYear AND dmonth=@SalaryMonth AND remarks='EL Deduction For Late')
			BEGIN
				SELECT @app_code=DedID,@deduction_day=DLeave FROM [LeaveDeduction] WHERE EmpId=@EmpId AND dyear=@SalaryYear AND dmonth=@SalaryMonth AND remarks='EL Deduction For Late' 

				EXEC sp_ManageLeaveDeduction 'Remove',@app_code,3,@EmpId,@SalaryMonth,@SalaryYear,@Late,@deduction_day,'EL Deduction For Late',100,0,@SalaryYear
			END
		END
		--late deduction end

		IF @UnitId NOT IN (1,27,45,13,14,12,111)
		BEGIN
			SET @PerDayGross=@GrossSalary/@MOD
			SET @PerDayBasic=ROUND(@BasicSalary,2)/30
			SET @PerDayConveyance=@PerDayGross-@PerDayBasic
			SET @PerDayLayOff=((@GrossSalary/100)*60)/@MOD
		END
		ELSE IF @UnitId IN (13,14)--sylhet
		BEGIN
			SET @PerDayGross=@GrossSalary/@MOD
			SET @PerDayBasic=ROUND(@GrossSalary,2)/@MOD
			SET @PerDayConveyance=ROUND(@GrossSalary-@BasicSalary,2)/@MOD

			SET @PerDayLayOff=((@GrossSalary/100)*60)/@MOD
		END
		ELSE IF @UnitId IN (19,58,112)--sample
		BEGIN
			SET @PerDayGross=@GrossSalary/@MOD
			SET @PerDayBasic=ROUND(@BasicSalary,2)/30
			SET @PerDayConveyance=ROUND(@PerDayGross-@PerDayBasic,2)

			SET @PerDayLayOff=((@GrossSalary/100)*60)/@MOD

		END
		ELSE
		BEGIN
			SET @PerDayGross=@GrossSalary/@MOD
			SET @PerDayBasic=ROUND(@BasicSalary,2)/@MOD
			SET @PerDayConveyance=ROUND(@GrossSalary-@BasicSalary,2)/@MOD
			SET @PerDayLayOff=((@GrossSalary/100)*60)/@MOD
			
		END
		

		SET @GrossPayAmt=@GrossDays*@PerDayGross
		SET @BasicPayAmt=@BasicDays*@PerDayBasic
		SET @AllowancePayAmt=@AllowanceDays*@PerDayConveyance
		SET @LayOffPayAmt=@LayOffDays*@PerDayLayOff
		
		

		
	


		SET	@AdvanceDed=@AdvanceDed+@AdvanceSalary	
		
		IF @ISLateSalaryDed=1 
			SET @LateDed=ROUND(@LateDedDay*@PerDayBasic,0)	

		IF @SalaryMonth=4 AND @SalaryYear=2020
		BEGIN
			SET @AbsentDed=(@Absent+@EE+@EXL+@NOP)*(@PerDayGross-@PerDayLayOff)
		END
		ELSE
		BEGIN
			IF @UnitId NOT IN (1,27,45,111)
			BEGIN
				IF @UnitId=19 AND @EmpTypeID = 1
				BEGIN
					SET @AbsentDed=(@Absent+@EE+@EXL+@NOP)*@PerDayGross
				END
				ELSE
				BEGIN
					SET @AbsentDed=(@Absent)*@PerDayBasic
				END
			END
			ELSE
			BEGIN
				SET @AbsentDed=(@Absent+@EE+@EXL+@NOP)*@PerDayGross
			END
		END	
		--SET	@AbsentDed=@Absent*@PerDayGross

		SET	@LWADed=@LWA*@PerDayGross
		SET @LODed=(@PerDayGross-@PerDayLayOff)*@LO
		SET @TiffinAmount=@TiffinDay*@TiffinRate
		SET @IfterAmount=@IfterDay*@IfterRate
		SET @HolidayAmount=@HolidayDay*@HolidayRate
		SET @NightAmount=@NightDay*@NightRate
		SET @NightIncentiveAmount=@NightIncentiveDay*@NightIncentiveRate
		SET @LunchAmount=@LunchDay*@LunchRate
		
		--//Attendance Bonus Calculation Start
		DECLARE @VShiftID int,@VLateCounter int=0,@VShiftLateCounter int=0
		IF @AttBonusRate>0
		BEGIN
			DECLARE ShiftCursor CURSOR FOR SELECT  ShiftID FROM Attendance_DailyAttendance WHERE EmpID=@EmpId AND MONTH(WorkDay) =@SalaryMonth AND YEAR(WorkDay) =@SalaryYear  GROUP BY  ShiftID
			OPEN ShiftCursor
			FETCH NEXT FROM ShiftCursor INTO @VShiftID
			WHILE @@FETCH_STATUS = 0
			BEGIN
				SET @VLateCounter=(SELECT COUNT(*) FROM Attendance_DailyAttendance WHERE EmpID=@EmpId AND ShiftID=@VShiftID AND AttStatus IN ('P','LTP') AND MONTH(WorkDay) = @SalaryMonth AND YEAR(WorkDay) = @SalaryYear AND CONVERT(VARCHAR(8),InTime,108)>(SELECT CONVERT(VARCHAR(8),[ShiftIn],108) FROM [Common_Shift] WHERE ShiftID = @VShiftID))
				SET @VShiftLateCounter=@VShiftLateCounter+@VLateCounter
			FETCH NEXT FROM ShiftCursor INTO @VShiftID
			END
			CLOSE ShiftCursor
			DEALLOCATE ShiftCursor
			
			IF @VShiftLateCounter=0
			BEGIN
				IF (@Late+@Absent+@CL+@EL+@ML+@SPL+@EE+@NOP+@EXL+@LWP+@LWA)=0 AND @MOD=@TotalDays
				BEGIN				
					SET @AttBonus=@AttBonusRate
				END
				ELSE
				BEGIN
					SET @AttBonus=0
				END
			END

		END
		--//Attendance Bonus Calculation End

		SELECT @SL=COUNT(*)+1 FROM Payroll_MonthlySalarySupplementary WHERE EmpId=@EmpId AND SalaryMonth=@SalaryMonth AND SalaryYear=@SalaryYear
			
		
	DECLARE @PreTotalDays int  ,							
	@PreMonthDays int,
	@PrePresent int  ,
	@PreAbsent int  ,
	@PreLate int  ,
	@PreLateDedDay int=0,
	@PreLWP int=0  ,
	@PreLWA int=0,
	@PreEL int=0  ,
	@PreCL int =0 ,
	@PreML int =0 ,
	@PreSPL int=0,
	@PreEE int=0,
	@PreWO int=0,
	@PreH int=0,
	@PreLO int=0,
	@PreGrossDays int =0 ,
	@PreBasicDays int =0 ,
	@PreAllowanceDays int=0  ,
	@PreLayOffDays int=0,
	@PreGrossSalary decimal(10,2)=0,
	@PreBasicSalary decimal(10,2)=0,
	@PreHouseRent decimal(10,2)=0,
	@PreMedicalAllowance decimal(10,2)=0,
	@PreConveyanceAllowance decimal(10,2)=0,
	@PreFoodingAllowance decimal(10,2)=0,
	@PreOthersAllowance decimal(10,2)=0,
	@PreKeyAllowance decimal(10,2)=0,
	@PreGrossPayAmt decimal(10,2)=0,
    @PreBasicPayAmt decimal(10,2)=0,
    @PreAllowancePayAmt decimal(10,2)=0,
	@PreLayOffPayAmt decimal(10,2)=0,
	@PreGrossPayable decimal(10,2)=0,
	@PreAdvanceDed decimal(10,2)=0,
	@PreAbsentDed decimal(10,2)=0,
	@PreLWADed decimal(10,2)=0,
	@PreTaxDed decimal(10,2)=0,
	@PreLoanDed decimal(10,2)=0,
	@PreLODed decimal(10,2)=0,
	@PrePenalti decimal(10,2)=0,
	@PreLateDed decimal(10,2)=0,
    @PreStampFee decimal(10,2)=0,
	@PreNetPayment decimal(10,2)=0,
	@PrePerDayGross decimal(10,2)=0,
	@PrePerDayBasic decimal(10,2)=0,
	@PrePerDayConveyance decimal(10,2)=0,
	@PrePerDayLayOff decimal(10,2)=0,
	@PreAdvanceSalary decimal (10,2)=0,
	@PreMobileAllowance decimal(10,2)=0,
	@PreTiffinRate decimal(10,2)=0,
    @PreTiffinDay int=0,
    @PreTiffinAmount decimal(10,2)=0,
	@PreIftarRate decimal(10,2)=0,
    @PreIftarDay int=0,
    @PreIftarAmount decimal(10,2)=0,
    @PreHolidayRate decimal(10,2)=0,
    @PreHolidayDay int =0,
    @PreHolidayAmount decimal(10,2)=0,
    @PreNightRate decimal(10,2)=0,
    @PreNightDay int=0,
    @PreNightAmount decimal(10,2)=0,
	@PreNightIncentiveDay int=0,
    @PreNightIncentiveAmount decimal(10,2)=0,
	@PreLunchRate decimal(10,2)=0,
    @PreLunchDay int=0,
    @PreLunchAmount decimal(10,2)=0,
    @PreOTHour DECIMAL(10,2)=0,
    @PreAttBonus decimal(10,2)=0,
	@PreDayAllowRate decimal(10,2)=0,
    @PreDayAllowDay int=0,
    @PreDayAllowAmount decimal(10,2)=0,
	@PreFestivalHolidayDay int=0
	
	DECLARE  @CurrentBasicDay int =(SELECT ISNULL((SELECT SUM(BasicDays) FROM Payroll_MonthlySalarySupplementary WHERE EmpId=@EmpId AND SalaryMonth=@SalaryMonth AND SalaryYear=@SalaryYear),0))
	DECLARE  @CurrentGrossDay int =(SELECT ISNULL((SELECT SUM(GrossDays) FROM Payroll_MonthlySalarySupplementary WHERE EmpId=@EmpId AND SalaryMonth=@SalaryMonth AND SalaryYear=@SalaryYear),0))
	DECLARE  @CurrentAllowanceDay int =(SELECT ISNULL((SELECT SUM(AllowanceDays) FROM Payroll_MonthlySalarySupplementary WHERE EmpId=@EmpId AND SalaryMonth=@SalaryMonth AND SalaryYear=@SalaryYear),0))
	DECLARE  @CurrentHoliday int=(SELECT ISNULL((SELECT SUM(HolidayDay) FROM Payroll_MonthlySalarySupplementary WHERE EmpId=@EmpId AND SalaryMonth=@SalaryMonth AND SalaryYear=@SalaryYear),0))
	DECLARE  @CurrentNightDay int=(SELECT ISNULL((SELECT SUM(NightDay) FROM Payroll_MonthlySalarySupplementary WHERE EmpId=@EmpId AND SalaryMonth=@SalaryMonth AND SalaryYear=@SalaryYear),0))
	DECLARE  @CurrentNightIncentiveDay int=(SELECT ISNULL((SELECT SUM(NightIncentiveDay) FROM Payroll_MonthlySalarySupplementary WHERE EmpId=@EmpId AND SalaryMonth=@SalaryMonth AND SalaryYear=@SalaryYear),0))
	DECLARE  @CurrentTiffinDay int=(SELECT ISNULL((SELECT SUM(TiffinDay) FROM Payroll_MonthlySalarySupplementary WHERE EmpId=@EmpId AND SalaryMonth=@SalaryMonth AND SalaryYear=@SalaryYear),0))
	DECLARE  @CurrentKeyAllowance int=(SELECT ISNULL((SELECT SUM(KeyAllowance) FROM Payroll_MonthlySalarySupplementary WHERE EmpId=@EmpId AND SalaryMonth=@SalaryMonth AND SalaryYear=@SalaryYear),0))
	DECLARE  @CurrentMobileAllowance int=(SELECT ISNULL((SELECT SUM(MobileAllowance) FROM Payroll_MonthlySalarySupplementary WHERE EmpId=@EmpId AND SalaryMonth=@SalaryMonth AND SalaryYear=@SalaryYear),0))
	DECLARE  @CurrentTaxDed int=(SELECT ISNULL((SELECT SUM(TaxDed) FROM Payroll_MonthlySalarySupplementary WHERE EmpId=@EmpId AND SalaryMonth=@SalaryMonth AND SalaryYear=@SalaryYear),0))
	DECLARE  @CurrentLoDay int =(SELECT ISNULL((SELECT SUM(LayoffDays) FROM Payroll_MonthlySalarySupplementary WHERE EmpId=@EmpId AND SalaryMonth=@SalaryMonth AND SalaryYear=@SalaryYear),0))
	DECLARE  @CurrentLunchDay int=(SELECT ISNULL((SELECT SUM(LunchDay) FROM Payroll_MonthlySalarySupplementary WHERE EmpId=@EmpId AND SalaryMonth=@SalaryMonth AND SalaryYear=@SalaryYear),0))
	DECLARE  @CurrentOTHour int=(SELECT ISNULL((SELECT SUM(OTHour) FROM Payroll_MonthlySalarySupplementary WHERE EmpId=@EmpId AND SalaryMonth=@SalaryMonth AND SalaryYear=@SalaryYear),0))
	DECLARE  @CurrentAttBonus int=(SELECT ISNULL((SELECT SUM(AttBonus) FROM Payroll_MonthlySalarySupplementary WHERE EmpId=@EmpId AND SalaryMonth=@SalaryMonth AND SalaryYear=@SalaryYear),0))

	SELECT	
	@PrePresent=Present ,
	@PreAbsent=	Absent ,
	@PreLate=Late ,
	@PreLateDedDay=LateDedDay,
	@PreLWP=ISNULL(LWP,0) ,
	@PreLWA=LWA,
	@PreEL=	EL ,
	@PreCL=	CL ,
	@PreML=	ML ,
	@PreSPL=SPL,
	@PreWO=WO,
	@PreH=H,
	@PreLO=LayoffDays,
	@PreGrossDays=ISNULL(GrossDays,0),
	@PreBasicDays=ISNULL(BasicDays,0),
	@PreAllowanceDays=AllowanceDays ,
	@PreGrossSalary =GrossSalary ,
	@PreBasicSalary =BasicSalary ,
	@PreHouseRent=HouseRent ,
	@PreMedicalAllowance =MedicalAllowance ,
	@PreConveyanceAllowance =ConveyanceAllowance ,
	@PreFoodingAllowance =FoodingAllowance ,
	@OthersAllowance =OthersAllowance,

	@PreGrossPayAmt =GrossPayAmt ,
	@PreBasicPayAmt =BasicPayAmt ,
	@PreAllowancePayAmt =AllowancePayAmt ,
	@PreLayoffPayAmt=LayOffPayAmt,
	@PreGrossPayable =GrossPayable ,
	@PreLayOffDays=LayOffDays,
	@PreAdvanceDed =AdvanceDed ,
	@PreAbsentDed =	AbsentDed ,
	@PreLWADed =LWADed ,
	@PreTaxDed =ISNULL(TaxDed,0),
	@PreLoanDed =LoanDed ,
	@PreLODed=LODed,
	@PrePenalti =Penalti ,
	@PreLateDed =LateDed ,
	@PreStampFee =StampFee ,
	@PreNetPayment =NetPayment ,
	@PreKeyAllowance =KeyAllowance ,
	@PreMobileAllowance =MobileAllowance ,
	@PreTiffinRate =TiffinRate ,
	@PreTiffinDay=	TiffinDay,
	@PreTiffinAmount =TiffinAmount ,
	@PreIftarRate=IftarRate,
	@PreIftarDay=IftarDay,
	@PreIftarAmount=IftarAmount,
	@PreHolidayRate =	HolidayRate ,
	@PreHolidayDay=	HolidayDay,
	@PreFestivalHolidayDay=FestivalHolidaysDay,
	@PreHolidayAmount =	HolidayAmount ,
	@PreNightRate =	NightRate ,
	@PreNightDay=	NightDay,
	@PreNightAmount =	NightAmount,
	@PreNightIncentiveDay=	NightIncentiveDay,
	@PreNightIncentiveAmount=	NightIncentiveAmount,
	@PreLunchRate =	LunchRate ,
	@PreLunchDay=	LunchDay,
	@PreLunchAmount =	LunchAmount,
	@PreOTHour=OTHour,
	@PreAttBonus=AttBonus

	FROM Payroll_MonthlySalary WHERE EmpId=@EmpId AND SalaryMonth=@SalaryMonth AND SalaryYear=@SalaryYear and UnPaid=0

 
	SET @PreGrossDays=@PreGrossDays+@CurrentGrossDay
	SET @PreBasicDays=@PreBasicDays+@CurrentBasicDay
	SET @PreAllowanceDays=@PreAllowanceDays+@CurrentAllowanceDay
	SET @PreHolidayDay=@PreHolidayDay+@CurrentHoliday
	SET @PreNightDay=@PreNightDay+@CurrentNightDay
	SET @PreNightIncentiveDay=@PreNightIncentiveDay+@CurrentNightIncentiveDay
	SET @PreTiffinDay=@PreTiffinDay+@CurrentTiffinDay
	SET @PreKeyAllowance=@PreKeyAllowance+@CurrentKeyAllowance
	SET @PreMobileAllowance=@PreMobileAllowance+@CurrentMObileAllowance
	SET @PreTaxDed=@PreTaxDed+@CurrentTaxDed
	SET @PreLO=@PreLO+@CurrentLoDay
	SET @PreOTHour=@PreOTHour+@CurrentOTHour
	SET @PreAttBonus=@PreAttBonus+@CurrentAttBonus

	--select @PreAllowanceDays,@AllowanceDays,@GrossDays
	 IF @PreAllowanceDays>=@AllowanceDays
	 BEGIN

		SET @BasicDays=@PreAllowanceDays-@AllowanceDays
		SET @GrossDays=@GrossDays-@BasicDays

			--select @PreAllowanceDays,@AllowanceDays,@GrossDays,@BasicDays

	 END

	 IF @PreAllowanceDays<@AllowanceDays 
	 BEGIN
		SET @AllowanceDays=@AllowanceDays-@PreAllowanceDays
	 END
	 ELSE
	 BEGIN
		SET @AllowanceDays=0
	 END	 

	 DECLARE @LOReturn int=0,@LOReturn1 int=0
	 IF @PreLO>= @LO
	 BEGIN
		SET @LOReturn1=@PreLO-@LO
	 END
	 ELSE
	 BEGIN
		SET @LOReturn=@LO-@PreLO
	 END	 
	 

	 SET @GrossDays=@GrossDays-@PreGrossDays-@CurrentBasicDay-@LOReturn1 --may be wrong
	
	

	 SET @GrossPayAmt=@GrossDays*@PerDayGross



	 SET @BasicPayAmt=@BasicDays*@PerDayBasic
	 SET @AllowancePayAmt=@AllowanceDays*@PerDayConveyance
	 --//Decipilanary Leave Start
	 IF @DS>0
	 BEGIN
	 	SET @BasicDays=@DS/2.00	
	 END
	 --//Decipilanary Leave END
	
	 IF @LOReturn1 >0
	BEGIN
		SET @LayOffPayAmt=(@PerDayGross-@PerDayLayOff)*(@LOReturn1)
	END
	ELSE IF @LOReturn >0 
	BEGIN
		SET @LayOffPayAmt=(@LOReturn*@PerDayLayOff)
	END
	ELSE
	BEGIN
		SET @LayOffPayAmt=0
	END

	 IF @PreTaxDed<@TaxDed
	 BEGIN
		SET @TaxDed=@TaxDed-@PreTaxDed
	 END
	 ELSE
	 BEGIN
		SET @TaxDed=0.00
	 END

	 
		--//Overtime Calculation Start
		IF @FixedOTRate>0
		BEGIN
			SET @OTRate=@FixedOTRate
		END
		ELSE
		BEGIN
			IF @EmpTypeID IN (3,4) AND @IsFixed=0
				SET @OTRate=ROUND(@BasicSalary/104.00,2)
			ELSE
				SET @OTRate=0
		END	


		SET @OTHour=@OTHour+(@OTMin/60) + CASE WHEN @UnitId IN (22,23,24) THEN (@OTMin%60)/100.00 ELSE 0 END

		IF @PreOTHour<@OTHour
		 BEGIN
			set @OTHour = ROUND(@OTHour, 2)
			SET @OTHour=@OTHour-@PreOTHour

			 -- Separate integer hours and fractional minutes
			DECLARE @FullHours INT
			DECLARE @FractionalMinutes DECIMAL(10, 2)

			SET @FullHours = FLOOR(@OTHour) -- Extract the integer part of the hours
			SET @FractionalMinutes = (@OTHour - @FullHours) * 100 -- Convert fractional hours to minutes

			-- Calculate the OTAmount for full hours
			DECLARE @FullHoursAmount DECIMAL(10, 2)
			SET @FullHoursAmount = @FullHours * @OTRate

			-- Calculate the OTAmount for fractional minutes
			DECLARE @FractionalAmount DECIMAL(10, 2)
			SET @FractionalAmount = (@OTRate / 60) * @FractionalMinutes

			-- Total OTAmount
			SET @OTAmount = @FullHoursAmount + @FractionalAmount

			-- Round OTAmount to 0 decimal places
			SET @OTAmount = ROUND(@OTAmount, 0)

			--SET @OTAmount=@OTRate*@OTHour
			--SET @OTAmount=ROUND(@OTAmount,
		 END
		 ELSE
		 BEGIN
			SET @OTHour=0.00
			SET @OTAmount=0.00
		 END
			
	--//Overtime Calculation End
		 
		 
	 IF @PreAttBonus<@AttBonus AND @Present>0
	 BEGIN
		SET @AttBonus=@AttBonus-@PreAttBonus
	 END
	 ELSE
	 BEGIN
		SET @AttBonus=0.00
	 END

	 IF @PreNetPayment < 0
	 BEGIN		
		SET @OverPay=ABS(@PreNetPayment)-@PreStampFee		
		IF @AdvanceAdd=ABS(@PreNetPayment)
		BEGIN
			SET @OverPay=0
		END
		SET @AdvanceDed=@AdvanceDed+@OverPay
	 END
	 
	 
	

	 IF @AdvanceDed>=@PreAdvanceDed
	 BEGIN
		SET @AdvanceDed=@AdvanceDed-@PreAdvanceDed
	 END

	 IF @Penalti>=@PrePenalti
	 BEGIN
		SET @Penalti=@Penalti-@PrePenalti
	 END

	
	IF (@UnitId IN (36))
	BEGIN
		SET @GrossPayable= @GrossPayAmt + @BasicPayAmt  + @LayOffPayAmt + @AttBonus
	END
	ELSE
	BEGIN	
	 SET @GrossPayable= @GrossPayAmt + @BasicPayAmt + @AllowancePayAmt + @LayOffPayAmt + @AttBonus
	END


	 -----change by rakibul alam 
	 SET @GrossPayable=@GrossPayable-@AdvanceDed
	 --SET @GrossPayable=@GrossPayable-(@AbsentDed+@AdvanceAdd)
	 --select @GrossPayable,@BasicPayAmt
	 --IF @GrossPayable>1000
	 --BEGIN
 	--	SET	@StampFee=10
	 --END		

	IF @UnitId IN (36)
		BEGIN
			SET @AbsentDed=(@Absent+@EE+@EXL+@NOP)*@PerDayGross
		END


		SET @PerDaySpecialAllowance=@SpecialAllowanceRate/@MOD


		IF @GrossDays=@MOD
		BEGIN
			SET @SpecialAllowance=@SpecialAllowanceRate
		END
		ELSE
		BEGIN
			SET @SpecialAllowance=@PerDaySpecialAllowance*@GrossDays
			IF @UnitId IN (31,32,39,87)
			BEGIN
				SET @SpecialAllowance=@PerDaySpecialAllowance*(@GrossDays+@AllowanceDays)
			END
			IF @UnitId IN (35)
			BEGIN
				SET @SpecialAllowance=@PerDaySpecialAllowance*(@GrossDays)
			END
		END


	SET @NetPayment=(@GrossPayable+@SpecialAllowance+@Adjustment)-(@StampFee+@TaxDed+@Penalti) 

	
	 
	--SET @NetPayment=@GrossPayable-(@TaxDed+@AdvanceDed+@Penalti+@PFDed)
	IF @NetPayment>1000
	 BEGIN
 		--SET	@StampFee=10
		--SET @NetPayment=@NetPayment-@StampFee
		SET @NetPayment=@NetPayment
	 END

	IF @PreKeyAllowance<@KeyAllowance
	BEGIN
		SET @KeyAllowance=@KeyAllowance-@PreKeyAllowance
	END
	ELSE
	BEGIN
		SET @KeyAllowance=0.00
	END

	IF @PreMobileAllowance<@MobileAllowance AND @PreMobileAllowance<>0
	BEGIN
		IF @PreGrossDays=0 AND @PreBasicDays=0 AND @PreAllowanceDays=0 AND @PreMobileAllowance=0 AND @GrossDays=0 AND @BasicDays=0 AND @AllowanceDays=0
		BEGIN
			SET @MobileAllowance=0.00
		END
		ELSE
		BEGIN
			SET @MobileAllowance=@MobileAllowance-@PreMobileAllowance
		END
	END
	ELSE
	BEGIN
		SET @MobileAllowance=0.00
	END
	
	IF @PreTiffinDay<@TiffinDay
	BEGIN
		SET @TiffinDay=@TiffinDay-@PreTiffinDay		
	END
	ELSE
	BEGIN
		SET @TiffinDay=0
	END
	SET @TiffinAmount=@TiffinDay*@TiffinRate

	IF @PreIftarDay<@IfterDay
	BEGIN
		SET @IfterDay=@IfterDay-@PreIftarDay		
	END
	ELSE
	BEGIN
		SET @IfterDay=0
	END
	SET @IfterAmount=@IfterDay*@IfterRate

	IF @PreHolidayDay<@HolidayDay
	BEGIN
		SET @HolidayDay=@HolidayDay-@PreHolidayDay		
	END
	ELSE
	BEGIN
		SET @HolidayDay=0
	END
	SET @HolidayAmount=@HolidayDay*@HolidayRate

	
	IF @PreFestivalHolidayDay<@FestivalHolidaysDay
	BEGIN
		IF(@EmpTypeID=1)
		BEGIN
			SET @FestivalHolidaysAmount=@FestivalHolidaysDay*@FestivalHolidaysRate
		END
		ELSE
		BEGIN
			SET @FestivalHolidaysRate=ROUND((@GrossSalary*2)/@MOD,0)
			SET @FestivalHolidaysAmount=@FestivalHolidaysDay*@FestivalHolidaysRate
		END

	END


	IF @PreNightDay<@NightDay
	BEGIN
		SET @NightDay=@NightDay-@PreNightDay		
	END
	ELSE
	BEGIN
		SET @NightDay=0
	END
	SET @NightAmount=@NightDay*@NightRate

	IF @PreNightIncentiveDay<@NightIncentiveDay
	BEGIN
		SET @NightIncentiveDay=@NightIncentiveDay-@PreNightIncentiveDay
	END
	ELSE
	BEGIN
		SET @NightIncentiveDay=0
	END
	SET @NightIncentiveAmount=@NightIncentiveDay*@NightIncentiveRate
	----comment by rakibul alam
	---SET @GrossPayable=@GrossPayable-@TaxDed


		
	SET @NetPayment=@NetPayment + @KeyAllowance+@MobileAllowance+@TiffinAmount+@IfterAmount+@HolidayAmount+@FestivalHolidaysAmount+@NightAmount+@NightIncentiveAmount+@OTAmount




	IF dbo.ReturnEmpCode(@EmpId) IN ('005457','065011')
	BEGIN
		SET @GrossDays=0
		SET @TotalDays=0
		SET @AllowanceDays=0
		SET @GrossPayAmt=0
		SET @BasicPayAmt=0
		SET @NetPayment=0
		SET @GrossPayable=0
	END




	DECLARE @Actiontime datetime=GETDATE(),@Unpaid bit=1
	--IF  @NetPayment>0
	BEGIN 
		INSERT INTO [dbo].[Payroll_MonthlySalarySupplementary]
           ([EmpId]
           ,[UnitId]
           ,[DepartmentId]
           ,[SectionId]
		   ,[WingId]
           ,[TeamId]
           ,[DesignationId]
		   ,[EmpTypeId]
		   ,[SalaryStructureId]
           ,[SalaryYear]
           ,[SalaryMonth]
		   ,[SL]
           ,[PaymentModeId]
           ,[BankId]
		   ,[BranchId]
		   ,[BankAccount]
           ,[PayrollCompanyId]
		   ,[MOD]
           ,[TotalDays]
           ,[Present]
           ,[Absent]
           ,[Late]
		   ,[LateDedDay]
           ,[LWP]
           ,[LWA]
           ,[EL]
           ,[CL]
           ,[ML]
           ,[WO]
           ,[H]
           ,[SPL]
		   ,[NOP]
		   ,[EE]
           ,[GrossDays]
           ,[BasicDays]
           ,[AllowanceDays]
		   ,[LayoffDays]
           ,[GrossSalary]
           ,[BasicSalary]
           ,[HouseRent]
           ,[MedicalAllowance]
           ,[ConveyanceAllowance]
		   ,[FoodingAllowance]
		   ,[OthersAllowance]
           ,[GrossPayAmt]
           ,[BasicPayAmt]
           ,[AllowancePayAmt]
		   ,[LayOffPayAmt]
           ,[GrossPayable]
		   ,[AttBonus]
           ,[AdvanceDed]
		   ,[Adjustment]
           ,[AbsentDed]
           ,[LWADed]
           ,[TaxDed]
           ,[LoanDed]
           ,[Penalti]
           ,[LateDed]
           ,[StampFee]
		   ,[SpecialAllowanceRate]
		   ,[SpecialAllowance]
           ,[NetPayment]
		   ,[KeyAllowance]
		   ,[MobileAllowance]
		   ,[TiffinRate]
		   ,[TiffinDay]
		   ,[TiffinAmount]
		   ,[IftarRate]
		   ,[IftarDay]
		   ,[IftarAmount]
		   ,[HolidayRate]
		   ,[HolidayDay]
		   ,[HolidayAmount]
		   ,[FestivalHolidaysRate]
		   ,[FestivalHolidaysDay]
		   ,[FestivalHolidaysAmount]
		   ,[NightRate]
		   ,[NightDay]
		   ,[NightAmount]
		   ,[NightIncentiveRate]
		   ,[NightIncentiveDay]
		   ,[NightIncentiveAmount]
		   ,[OTRate]
		   ,[OTHour]
		   ,[OTAmount]
		   ,[ProcessBy]
		   ,[ProcessDate]
		   ,PFDed
		   ,SalaryMood
		   ,Unpaid
		   
		   )
     VALUES
           (@EmpId
           ,@UnitId
           ,@DepartmentId
           ,@SectionId
		   ,@WingId
		   ,@TeamId
           ,@DesignationId
		   ,@EmpTypeID
		   ,@SalaryStractureId
           ,@SalaryYear
           ,@SalaryMonth
		   ,@SL
           ,@PaymentModeId
           ,@BankId
		   ,@BranchId
		   ,@BankAccount
           ,@PayrollCompanyId
		   ,@MOD
           ,ISNULL(@TotalDays,0)
           ,@Present
           ,@Absent
           ,@Late
		   ,@LateDedDay
           ,@LWP
           ,@LWA
           ,@EL
           ,@CL
           ,@ML
           ,@WO
           ,@H
           ,@SPL
		   ,@NOP
		   ,@EE
           ,@GrossDays
           ,@BasicDays
           ,@AllowanceDays
		   ,@LOReturn+@LOReturn1
           ,@GrossSalary
           ,ROUND(@BasicSalary,0)
           ,ROUND(@HouseRent,0)
           ,ROUND(@MedicalAllowance,0)
           ,ROUND(@ConveyanceAllowance,0)
		   ,ROUND(@FoodingAllowance,0)
		   ,ROUND(@OthersAllowance,0)
           ,ROUND(@GrossPayAmt,0)
           ,ROUND(@BasicPayAmt,0)
           ,ROUND(@AllowancePayAmt,0)
		   ,ROUND(@LayOffPayAmt,0)
           ,ROUND(@GrossPayable,0)
		   ,@AttBonus
           ,@AdvanceDed
		   ,@Adjustment
           ,@AbsentDed
           ,0
           ,@TaxDed
           ,ISNULL(@LoanDed,0)
           ,@Penalti
           ,@LateDed
           ,@StampFee
		   ,@SpecialAllowanceRate
		   ,@SpecialAllowance
           ,ROUND(@NetPayment,0)
		   ,@KeyAllowance
		   ,@MobileAllowance
		   ,@TiffinRate
		   ,@TiffinDay
		   ,@TiffinAmount
		   ,@IfterRate
		   ,@IfterDay
		   ,@IfterAmount
		   ,@HolidayRate
		   ,@HolidayDay
		   ,@HolidayAmount
		   ,@FestivalHolidaysRate
		   ,@FestivalHolidaysDay
		   ,@FestivalHolidaysAmount
		   ,@NightRate
		   ,@NightDay
		   ,@NightAmount
		   ,@NightIncentiveRate
		   ,@NightIncentiveDay
		   ,@NightIncentiveAmount
		   ,@OTRate
		   ,@OTHour
		   ,@OTAmount
		   ,@p_UserId
		   ,GETDATE()
		   ,@PFDed
		   ,@SalaryMood
		   ,@Unpaid
		   )
	END
	END --Main END

	TheEnd:
END


--SELECT * FROM Payroll_MonthlySalaryAdvance WHERE EmpId=@EmpId AND SalaryMonth=@SalaryMonth AND SalaryYear=@SalaryYear
--SELECT * FROM Payroll_MonthlySalary WHERE EmpId=@EmpId AND SalaryMonth=@SalaryMonth AND SalaryYear=@SalaryYear
--SELECT NetPayment,GrossPayable,* FROM Payroll_MonthlySalarySupplementary WHERE EmpId=@EmpId AND SalaryMonth=@SalaryMonth AND SalaryYear=@SalaryYear



--delete from Payroll_MonthlySalarySupplementary where empid=@EmpId and ActionTime>='2024-05-18'












