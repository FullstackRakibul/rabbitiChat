USE [CoreERP]
GO
/****** Object:  StoredProcedure [dbo].[ProcessFinalSettelment]    Script Date: 14-Oct-2026 11:47:07 AM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
ALTER PROCEDURE [dbo].[ProcessFinalSettelment] @EmpID bigint,@p_UserId int AS
BEGIN
	--DECLARE @EmpID bigint=(SELECT EmpID FROM HumanResource_EmployeeBasic WHERE EmpCode='156754'),@p_UserId int=0
	
	DECLARE @YearlyAllocation decimal(10,2),@ActionDate date,@AllocationAfterDays int,@EmpCategoryID int,@WorkingDaysForPerLeave decimal,@WorkingDay int,@Allocation decimal(10,2)

	DECLARE @JoiningDate date,
		    @SeparationEffectDate date,
			@NoticePeriod int,
			@IsWaive bit,
			@EmpStatusID int,
			@ELBalance decimal(10,2),
			@UnpaidSalary decimal=0,
			@GrossDays int,
			@AllowanceDays int,
			@PayrollCompanyId int,
			@GrossSalary decimal(10,2),
			@BasicSalary decimal(10,2),
			@OTRate decimal(10,2),
			@UnitId int ,
			@DepartmentId int ,
			@SectionId int ,
			@WingId int,
			@TeamId int,
			@DesignationId int,
			@PaymentModeId int  ,
			@EmpTypeID int,
			@CurrencyId int,
			@BankId int ,
			@BranchId int,
			@BankAccount nvarchar(50),
			@ELAmount decimal=0.00,
		    @TiffinAllowance decimal=0.00,
            @HolidayAllowance decimal=0.00,
            @NightAllowance decimal=0.00,
            @MobileAllowance decimal=0.00,
			--@LengthYear int,
			@LengthYear decimal(10,2),
			@LengthMonth int,
			@LengthDays int,
			@GratuityAmount decimal=0.00,
			@ServiceBenefitAmount decimal=0.00,
			@DeathBenefitAmount decimal=0.00,
			@TerminationBenefit decimal=0.00,
			@RemovalBenefit decimal=0.00,
			@RetirmentBenefitAmount decimal=0.00,
			@ExpirationBenefitAmount decimal =0.00,
			@DischargeBenefitAmount decimal =0.00,
			@BonusAmount decimal=0.00,
			@OtherAllowance decimal=0.00,
			@ProvidentFundAmount decimal=0.00,
			@NoticePeriodPayableDays int,
			@NoticePeriodPayableAmount decimal=0.00,
			@SuspensionPayday int,
			@SuspensionPayAmount decimal=0.00,
			@LoanDeduction decimal=0.00,
			@NoticePeriodDeductionDay int,
			@NoticePeriodDeductionAmount decimal=0.00,
			@OtherDeduction decimal=0.00,
			@TaxAmt decimal=0.00,
			@StampFee decimal=0.00,
			@NetPayableAmount decimal=0.00,
			@Remarks varchar(500),
			@DLeave decimal=0.00,
			@benefit DECIMAL(10, 2)
			




	   SELECT  @JoiningDate=JoiningDate,
			   @SeparationEffectDate=EffectDate,
			   @NoticePeriod=NoticePeriod,
			   @IsWaive=IsWaive,
			   @EmpStatusID=ST.EmpStatusID,
			   @EmpCategoryID=EB.EmpCategoryID,
			   @UnitId=EB.UnitID,
			   @DepartmentId=EB.DepartmentID,
			   @SectionId=EB.SectionID,
			   @WingId=WingID,
			   @TeamId=TeamID,
			   @DesignationId=EB.DesignationID,
			   @EmpTypeID=EB.EmpTypeID,
			   @CurrencyId=BS.CurrencyId,
			   @BankId=BS.BankId,
			   @BranchId=BS.BranchId,
			   @BankAccount=BS.BankAccountNo,
			   @PaymentModeId=BS.PaymentModeId,
			   @PayrollCompanyId=BS.SalaryPaidCompany,
			   @GrossSalary=BS.CurrentSalary,
			   @Remarks=Remarks
	FROM HumanResource_EmployeeBasic EB 
		 INNER JOIN HumanResource_EmployeeSeparation ES ON EB.EmpID=ES.EmpID 
		 INNER JOIN Common_EmploymentStatus ST ON ES.EmpStatusID=ST.EmpStatusID	
		 LEFT JOIN Payroll_Employee_BasicSalary BS ON EB.EmpID=BS.EmpID	
	WHERE EB.EmpID=@EmpID AND EB.EmpStatusID != 1 AND ES.IsActive=1 AND ST.IsPartial=0


	IF @IsWaive=1
	BEGIN
		SET @NoticePeriod=0
	END
	 

	 ------------ Updated Sattlement -----RH

	 --DECLARE @ENDDATE DATE , @YearCompleteDateAfterLastPayment DATE ,  @EffectiveWorkDate DATE, @WorkingDayCalculationYear int = year(@SeparationEffectDate) , @EffectSalaryMonth int;
	 --DECLARE	@SalaryMonth int=CASE WHEN DATEPART(DAY,@SeparationEffectDate)=1 THEN MONTH(DATEADD(DAY,-1,@SeparationEffectDate)) ELSE MONTH(@SeparationEffectDate) END
		--	,@SalaryYear int=@WorkingDayCalculationYear
		
	------------//Added Sattlement salaryMonth,Year for 27 to 26-----Rakibul

	 DECLARE @ENDDATE DATE , @YearCompleteDateAfterLastPayment DATE ,  @EffectiveWorkDate DATE, @WorkingDayCalculationYear int = year(@SeparationEffectDate) , @EffectSalaryMonth int,@SalaryMonth int,@SalaryYear int;
	 IF @UnitId IN (1,27,45,111) AND ((DAY(@SeparationEffectDate) BETWEEN 27 AND 31) OR DAY(@SeparationEffectDate)=1)
	 BEGIN 
	 	 SET @SalaryMonth =MONTH(DATEADD(DAY,5,@SeparationEffectDate))
		 SET @SalaryYear =YEAR(DATEADD(DAY,5,@SeparationEffectDate))
	 END 
	 ELSE
	 BEGIN
	 	 SET @SalaryMonth =CASE WHEN DATEPART(DAY,@SeparationEffectDate)=1 THEN MONTH(DATEADD(DAY,-1,@SeparationEffectDate)) ELSE MONTH(@SeparationEffectDate) END
		 SET @SalaryYear =@WorkingDayCalculationYear
	 END
	 ------------//END-------

	 EXEC ProcessSupplementaryFinalSetelment @EmpID,@SalaryMonth,@SalaryYear,@p_UserId



	IF @EmpStatusID=8 SET @NoticePeriod=0  

	--// Current Date to lefty date difference must be 28 days
	IF @EmpStatusID=5 AND CAST(GETDATE() as date)<= DATEADD(DAY,28,@SeparationEffectDate)
	BEGIN
		DELETE FROM FinalSattlementPayable WHERE EmpID=@EmpID
		GOTO TheEND;
	END


	--//Cacculate Current Year Leave Start
		IF DATEDIFF(DAY,@JoiningDate,@SeparationEffectDate)<365
		BEGIN
			--SET @ELBalance=0

			--//Added by rakibul deduct the EL for less than 1 year workduration
			SET  @ELBalance = ISNULL(@ELBalance , 0) - 
				(ISNULL ((SELECT sum(DLeave) from LeaveDeduction where EmpID=@EmpID AND (DYear * 100 + DMonth) > (YEAR(@JoiningDate) * 100 + MONTH(@JoiningDate))),0)+
				((ISNULL ((SELECT SUM(DedDayCount) from Earned_Leave_Deduction_By_Date where EmpID=@EmpID AND WorkDay>=@JoiningDate AND ModeId=1),0)+
				(SELECT COUNT(EmpID) FROM Attendance_DailyAttendance WHERE WorkDay>=@JoiningDate AND EmpID=@EmpID AND AttStatus='AL'))))
		END
		ELSE
		BEGIN
		
			--SELECT @DLeave = SUM(DLeave)
			--FROM
			--(
			--SELECT EmpID,DYear,LeaveID,DLeave FROM LeaveDeduction WHERE EmpID=@EmpID AND DYear = YEAR(@SeparationEffectDate)
			--UNION ALL
			--SELECT EmpId,PayYear,3,PayableDay DLeave FROM Payroll_AnnualLeave_Payment WHERE EmpID=@EmpID AND PayYear = YEAR(@SeparationEffectDate)
			--) ltable GROUP BY EmpID

			--SELECT @ELBalance=LeaveAllocated-(LeaveUsed + @DLeave)


			SELECT @ELBalance=LeaveAllocated-(LeaveUsed)
				FROM LeaveAllocation WHERE EmpId=@EmpID AND LeaveId=3 AND LeaveYear=YEAR(@SeparationEffectDate);

	
				
		
		

			--Previous Code

			--WITH LastResponse
			--AS
			--(
			--	SELECT ROW_NUMBER() OVER(PARTITION BY EB.EmpID,LM.LeaveID,EB.GenderID,EB.JoiningDate,EB.EmpCategoryID ORDER BY ActionDate DESC ) AS OrderId, 

			--	EB.EmpID,LM.LeaveID,EB.GenderID,EB.JoiningDate,LM.YearlyAllocation,LM.IsHalfDayAllow,LM.ActionDate,LM.AllocationAfterDays,EB.EmpCategoryID,LM.WorkingDaysForPerLeave

			--	FROM	HumanResource_EmployeeBasic EB
			--			INNER JOIN LeaveAllocationMaster LM ON EB.EmpCategoryID=LM.EmpCategoryID AND (CASE WHEN LM.GenderID=0 THEN EB.GenderID ELSE LM.GenderID END) = EB.GenderID						
			--	WHERE   YEAR(ActionDate)<=YEAR(@SeparationEffectDate) AND EB.EmpID=@EmpId AND LM.LeaveID=3
			--)
			--SELECT @YearlyAllocation=YearlyAllocation,@ActionDate=ActionDate,@AllocationAfterDays=AllocationAfterDays,@EmpCategoryID=EmpCategoryID,@WorkingDaysForPerLeave=WorkingDaysForPerLeave FROM LastResponse WHERE OrderId = 1;	
			SELECT TOP 1 @YearlyAllocation=YearlyAllocation,@ActionDate=ActionDate,@AllocationAfterDays=AllocationAfterDays,
			@EmpCategoryID=EmpCategoryID,@WorkingDaysForPerLeave=WorkingDaysForPerLeave
			FROM LeaveAllocationMaster WHERE EmpCategoryID=@EmpCategoryID and EmpTypeID=@EmpTypeID AND LeaveID=3 ORDER BY ActionDate DESC


			
			--END--



			--IF @WorkingDaysForPerLeave >0
			--BEGIN
			--	SET @WorkingDay=(SELECT COUNT (*) FROM Attendance_DailyAttendance WHERE YEAR(WorkDay)=YEAR(@SeparationEffectDate) AND EmpID=@EmpId AND AttStatus IN ('P','LT','LTP','SD'))
					
			--	IF @WorkingDay IS NOT NULL AND @WorkingDay>0
			--	BEGIN
			--		SET @Allocation=@WorkingDay/18.00
			--	END
			--END
			--ELSE
			--BEGIN
			--	DECLARE @SeparationEffectDate1 DATE=@SeparationEffectDate

			--	IF DAY(@SeparationEffectDate) <= 15 
			--	BEGIN
			--		SET @SeparationEffectDate1= DATEADD(M,-1,@SeparationEffectDate)
			--	END

			--	SET @Allocation= CASE MONTH(@SeparationEffectDate1) 
			--			WHEN  1 THEN (@YearlyAllocation/12.00)*01.00
			--			WHEN  2 THEN (@YearlyAllocation/12.00)*02.00
			--			WHEN  3 THEN (@YearlyAllocation/12.00)*03.00
			--			WHEN  4 THEN (@YearlyAllocation/12.00)*04.00
			--			WHEN  5 THEN (@YearlyAllocation/12.00)*05.00
			--			WHEN  6 THEN (@YearlyAllocation/12.00)*06.00
			--			WHEN  7 THEN (@YearlyAllocation/12.00)*07.00
			--			WHEN  8 THEN (@YearlyAllocation/12.00)*08.00
			--			WHEN  9 THEN (@YearlyAllocation/12.00)*09.00
			--			WHEN 10 THEN (@YearlyAllocation/12.00)*10.00
			--			WHEN 11 THEN (@YearlyAllocation/12.00)*11.00
			--			WHEN 12 THEN (@YearlyAllocation/12.00)*12.00
			--	END
			--END
			
			
			
			IF @EmpTypeID=1
			BEGIN
				--SET @WorkingDay=(SELECT COUNT (*) FROM Attendance_DailyAttendance WHERE YEAR(WorkDay)=YEAR(@SeparationEffectDate) AND EmpID=@EmpId AND AttStatus IN ('P','LT','LTP','SD','TL'))
				DECLARE @SeparationEffectDate1 DATE=@SeparationEffectDate
				IF (@UnitId in (1,27,45))
				BEGIN
					SET @WorkingDay=DATEDIFF(DAY,(CONVERT(DATETIME, '01/01/' + STR(YEAR(@SeparationEffectDate)))),@SeparationEffectDate)
					set @Allocation = (16.00/360.00)*@WorkingDay


					
				END
				ELSE
				BEGIN
				
					IF DAY(@SeparationEffectDate) <= 15 
					BEGIN
						SET @SeparationEffectDate1= DATEADD(M,-1,@SeparationEffectDate)

					END

					SET @Allocation= CASE MONTH(@SeparationEffectDate1) 
							WHEN  1 THEN Round((@YearlyAllocation/12.00),2)*01.00
							WHEN  2 THEN Round((@YearlyAllocation/12.00),2)*02.00
							WHEN  3 THEN Round((@YearlyAllocation/12.00),2)*03.00
							WHEN  4 THEN Round((@YearlyAllocation/12.00),2)*04.00
							WHEN  5 THEN Round((@YearlyAllocation/12.00),2)*05.00
							WHEN  6 THEN Round((@YearlyAllocation/12.00),2)*06.00
							WHEN  7 THEN Round((@YearlyAllocation/12.00),2)*07.00
							WHEN  8 THEN Round((@YearlyAllocation/12.00),2)*08.00
							WHEN  9 THEN Round((@YearlyAllocation/12.00),2)*09.00
							WHEN 10 THEN Round((@YearlyAllocation/12.00),2)*10.00
							WHEN 11 THEN Round((@YearlyAllocation/12.00),2)*11.00
							WHEN 12 THEN Round((@YearlyAllocation/12.00),2)*12.00

					END

					--//NEW CALCULATION FOR AGL, SHOULD BE FOR ALL --TAMAL

					

					IF @UnitId IN (54,55,61,90 , 46,47,48,91)
					BEGIN
						

						DECLARE @LastYearFirstWorkMonth INT=ISNULL(CASE
															WHEN @JoiningDate>CAST(CAST(YEAR(@SeparationEffectDate) AS VARCHAR)+'-01-01' AS DATE)
																THEN MONTH(@JoiningDate) ELSE 1
															END,1)

						DECLARE @LastMonthLastDay INT=DAY(EOMONTH(@SeparationEffectDate))

						DECLARE @LastMonthWorkDay INT=(SELECT COUNT(WorkDay) FROM Attendance_DailyAttendance WHERE EmpID=@EmpID 
							AND YEAR(WorkDay)=YEAR(@SeparationEffectDate) AND MONTH(WorkDay)=MONTH(@SeparationEffectDate))

						DECLARE @LastYearFullMonth INT=(MONTH(@SeparationEffectDate)-@LastYearFirstWorkMonth)

						DECLARE @LastYearFullMonthAllocation DECIMAL(5,2)=@LastYearFullMonth*1.33

						DECLARE @LastYearFractionMonthAllocation DECIMAL(5,2)=(1.33/@LastMonthLastDay)*@LastMonthWorkDay

						SET @Allocation=ROUND(@LastYearFullMonthAllocation+@LastYearFractionMonthAllocation,2)

					END
					

					

					
					
					------------------------ RAKIBUL
					IF EXISTS (SELECT * FROM Payroll_AnnualLeave_Payment_JoinYear_Fraction WHERE EmpId=@EmpID )
					BEGIN 
						SELECT TOP 1 @YearCompleteDateAfterLastPayment = YearCompleteDateAfterLastPayment, @ENDDATE=EndDate FROM Payroll_AnnualLeave_Payment_JoinYear_Fraction WHERE EmpId=@EmpID ORDER BY YearCompleteDateAfterLastPayment DESC;
						SET @EffectiveWorkDate = ISNULL(@YearCompleteDateAfterLastPayment, @ENDDATE);



						-- Old EL/AL 
						set @ELBalance=0
					END
					ELSE SET @EffectiveWorkDate = @JoiningDate

					---------------------
					
				

					--//END

				END

			END

			ELSE
			BEGIN

			-----------------:::::::::::::::::  @WorkingDay Count :::::::::::::::::::::::::::::::::::: this section may need to remove , 
				


				-- NEED TO CHECK IF EVER GET ANNUAL PAYMENT , SO CHEKC THE FOLLOWNG TABLE ON ORDER 

					--SELECT * FROM Payroll_AnnualLeave_Payment WHERE EmpId=@EmpID
					--SELECT * FROM Payroll_AnnualLeave_Payment_JoinYear_Fraction WHERE EmpId=@EmpID
					--SELECT * FROM Payroll_AnnualLeave_Payment_JoinDate WHERE EmpId=@EmpID
					--SELECT * FROM Payroll_AnnualLeave_Payment_Monthly WHERE EmpId=@EmpID


					IF EXISTS (SELECT * FROM   Payroll_AnnualLeave_Payment_JoinYear_Fraction WHERE  EmpId = @EmpID) --AND @SeparationEffectDate < '2025-04-01'
						BEGIN

							SELECT   TOP 1 @YearCompleteDateAfterLastPayment = YearCompleteDateAfterLastPayment,
										   @ENDDATE = EndDate
							FROM     Payroll_AnnualLeave_Payment_JoinYear_Fraction
							WHERE    EmpId = @EmpID
							ORDER BY YearCompleteDateAfterLastPayment DESC;
							SET @EffectiveWorkDate = ISNULL(@YearCompleteDateAfterLastPayment, @ENDDATE);
							-- Old EL/AL 
							SET @ELBalance = 0;
						END


					IF EXISTS (SELECT * FROM   Payroll_AnnualLeave_Payment_JoinDate WHERE  EmpId = @EmpID)
						AND @SeparationEffectDate >= '2025-04-01'
						BEGIN
							SELECT TOP 1 @ENDDATE = EndDate
							FROM   Payroll_AnnualLeave_Payment_JoinDate
							WHERE  EmpId = @EmpID;
							SET @EffectiveWorkDate = @ENDDATE;
						END


			
					IF EXISTS (SELECT * FROM   Payroll_AnnualLeave_Payment_Monthly WHERE  EmpId = @EmpID) AND @SeparationEffectDate >= '2025-04-01'
						BEGIN
							SELECT TOP 1 @ENDDATE = EndDate
							FROM   Payroll_AnnualLeave_Payment_Monthly
							WHERE  EmpId = @EmpID;
							SET @EffectiveWorkDate = @ENDDATE;
						END
						
								
								
						--SET @EffectiveWorkDate = '2024-01-01'; -- MAY BR WRONG


						



				-- For New Year
				IF MONTH(@SeparationEffectDate) = 1
				BEGIN
					SET @WorkingDayCalculationYear = YEAR(@SeparationEffectDate) - 1;
					SET @EffectSalaryMonth = 12;

				END
				ELSE
				BEGIN
					SET @WorkingDayCalculationYear = YEAR(@SeparationEffectDate);
					SET @EffectSalaryMonth = MONTH(@SeparationEffectDate);

				END;

				IF @UnitId IN (36)
				BEGIN
					SELECT @EffectiveWorkDate = DATEFROMPARTS(@WorkingDayCalculationYear,01,01)
				END


				--YEAR(WorkDay)=YEAR(@SeparationEffectDate) //  lAST PAYMENT ER POR THE JEHETU WORK DAY COUNT HOYTECHE , TAI YEAR RESTRICTION RAKHLAM NA



				SET @WorkingDay=(SELECT COUNT (*) FROM Attendance_DailyAttendance
				WHERE EmpID=@EmpId AND AttStatus IN ('P','LT','LTP','SD','TL','EE','NOP','EXL') AND WorkDay > @EffectiveWorkDate)  


				IF @UnitId IN (46,47,48,91) and @SeparationEffectDate<'2025-01-01'
				BEGIN
					SET @WorkingDay=(SELECT COUNT (*) FROM Attendance_DailyAttendance WHERE WorkDay >='2024-07-01'
						AND EmpID=@EmpId AND AttStatus IN ('P','LT','LTP','SD','TL','EE','NOP','EXL') AND WorkDay > @EffectiveWorkDate) + (SELECT WorkDays FROM Attendance_MigratedEmployee WHERE EmpID=@EmpID) 
				
				END	
				


				--IF @UnitId IN (54,55,61,90)  and @SeparationEffectDate<'2025-01-01'-- For AGL 
				--BEGIN

				--	SET @WorkingDay=(SELECT COUNT (*) FROM Attendance_DailyAttendance WHERE YEAR(WorkDay)=YEAR(@SeparationEffectDate) and WorkDay>='2024-09-01'
				--		AND EmpID=@EmpId AND AttStatus IN ('P','LT','LTP','SD','TL','EE','NOP','EXL')) +  isnull( (SELECT WorkDays FROM Attendance_MigratedEmployee WHERE EmpID=@EmpID),0)
				--		--SELECT WorkDays as CheckOldDays FROM Attendance_MigratedEmployee WHERE EmpID=@EmpID -- check migrated days

				--		SELECT @WorkingDay
				--END

				IF @UnitId IN (46,47,48,91) and @SeparationEffectDate<'2025-01-01' -- Only for ADL
				BEGIN

					SET @WorkingDay=(SELECT COUNT (*) FROM Attendance_DailyAttendance WHERE YEAR(WorkDay)=YEAR(@SeparationEffectDate) and WorkDay>='2024-07-01'
						AND EmpID=@EmpId AND AttStatus IN ('P','LT','LTP','SD','TL','EE','NOP','EXL') AND WorkDay > @EffectiveWorkDate) +  isnull( (SELECT WorkDays FROM Attendance_MigratedEmployee WHERE EmpID=@EmpID),0)
						--SELECT WorkDays as CheckOldDays FROM Attendance_MigratedEmployee WHERE EmpID=@EmpID
				
				END


				IF @WorkingDay IS NOT NULL AND @WorkingDay>0
				BEGIN
					SET @Allocation=@WorkingDay/18.00
				END

			END		
			

			

			----Just for 015405
					IF @EmpID = (SELECT EmpId FROM HumanResource_EmployeeBasic WHERE EmpCode='015405')
					BEGIN
						SET @WorkingDay = 198
						SET @Allocation=11.96
					END
					

					--- End ---

		----Just for 041870
			IF @EmpID=DBO.REmpId('041870') 
			SET @Allocation = 0.74;
			--END--

					
			
			-- EL BALANCE CALCULATION AFETR THE PAYMENT

			IF EXISTS (SELECT * FROM Payroll_AnnualLeave_Payment_JoinYear_Fraction WHERE EmpId=@EmpID) OR EXISTS (SELECT * FROM Payroll_AnnualLeave_Payment_JoinDate WHERE EmpId=@EmpID ) 
				BEGIN 
					--SET  @ELBalance = ISNULL(@ELBalance , 0) + isnull(@Allocation,0) - 
					--(ISNULL ((SELECT sum(DLeave) from LeaveDeduction where EmpID=@EmpID AND (DYear * 100 + DMonth) > (YEAR(@EffectiveWorkDate) * 100 + MONTH(@EffectiveWorkDate))),0)+
					--(SELECT COUNT(EmpID) FROM Attendance_DailyAttendance WHERE WorkDay>=@EffectiveWorkDate AND EmpID=@EmpID AND AttStatus='AL'))



					SELECT @EffectiveWorkDate=(SELECT TOP 1 DATEADD(DAY,1,EndDate) FROM Payroll_AnnualLeave_Payment_JoinDate WHERE EmpId=@EmpID ORDER BY EndDate DESC)

					IF @EffectiveWorkDate IS NULL AND EXISTS (SELECT EndDate FROM Payroll_AnnualLeave_Payment_JoinYear_Fraction WHERE EmpId=@EmpID)
					BEGIN
						SET @EffectiveWorkDate=DATEFROMPARTS(2024,MONTH(@JoiningDate),DAY(@JoiningDate))
					END

					

					IF @EffectiveWorkDate<='2024-11-30'
					BEGIN
						SET @EffectiveWorkDate=DATEFROMPARTS(IIF(MONTH(@JoiningDate)=12,2024,2025),MONTH(@JoiningDate),DAY(@JoiningDate))
					END



					SET @WorkingDay=(SELECT COUNT (*) FROM Attendance_DailyAttendance
						WHERE EmpID=@EmpId AND AttStatus IN ('P','LT','LTP','SD','TL','EE','NOP','EXL') AND WorkDay >= @EffectiveWorkDate)  

					
					SET @ELBalance=@WorkingDay/18.0



					SET  @ELBalance = ISNULL(@ELBalance , 0) - 
					(ISNULL ((SELECT sum(DLeave) from LeaveDeduction where EmpID=@EmpID AND (DYear * 100 + DMonth) > (YEAR(@EffectiveWorkDate) * 100 + MONTH(@EffectiveWorkDate))),0)+
					(ISNULL ((SELECT SUM(DedDayCount) from Earned_Leave_Deduction_By_Date where EmpID=@EmpID AND WorkDay>=@EffectiveWorkDate AND WorkDay<=@SeparationEffectDate AND ModeId=1),0)+
					(SELECT COUNT(EmpID) FROM Attendance_DailyAttendance WHERE WorkDay>=@EffectiveWorkDate AND EmpID=@EmpID AND AttStatus='AL')))
					
					--SELECT 'T',@ELBalance,@WorkingDay,@EffectiveWorkDate,@SeparationEffectDate,@JoiningDate

				END
				ELSE
				BEGIN
					SET @ELBalance=ISNULL(@ELBalance , 0)+isnull(@Allocation,0)
				END
		END


		



		
	--//Cacculate Current Year Leave End

	IF @ELBalance>60 and @EmpTypeID <>2
	BEGIN
		SET @ELBalance=60
	END

	IF @ELBalance>40 and @EmpTypeID =2
	BEGIN
		SET @ELBalance=40
	END

	--DECLARE	@SalaryMonth int=CASE WHEN DATEPART(DAY,@SeparationEffectDate)=1 THEN MONTH(DATEADD(DAY,-1,@SeparationEffectDate)) ELSE MONTH(@SeparationEffectDate) END
	--		,@SalaryYear int=@WorkingDayCalculationYear
	DECLARE @date DATETIME=CONVERT(DATETIME, STR(@SalaryMonth) + '/01/' + STR(@SalaryYear))	
	DECLARE @ContStartDate date
	SELECT @ContStartDate=max(ContractStartDate) FROM Employee_ContractInfo 
	WHERE isnull(IsDeleted,0)=0  and YEAR(ContractStartDate)=@SalaryYear AND MONTH(ContractStartDate)=@SalaryMonth AND EmpId=@EmpId
	GROUP BY EmpId

	--//Calculate Salary Stracture Start

	IF EXISTS (SELECT * FROM Payroll_AnnualLeave_Payment_JoinYear_Fraction WHERE EmpId=@EmpID)
	BEGIN
		IF(@UnitId in (1,27,45))
		BEGIN
			SELECT @GrossSalary=GrossSalary,@BasicSalary=BasicSalaryOrg
			FROM [dbo].[GetSalaryDetails] (@EmpId,MONTH(@SeparationEffectDate),YEAR(@SeparationEffectDate),1,@ContStartDate)
		END
		ELSE
		BEGIN
			SELECT @GrossSalary=GrossSalary,@BasicSalary=BasicSalary
			FROM [dbo].[GetSalaryDetails] (@EmpId,MONTH(@SeparationEffectDate),YEAR(@SeparationEffectDate),1,@ContStartDate)
	END
	END
	ELSE
	BEGIN
		IF(@UnitId in (1,27,45))
		BEGIN
			SELECT @GrossSalary=GrossSalary,@BasicSalary=BasicSalaryOrg
			FROM [dbo].[GetSalaryDetails] (@EmpId,MONTH(@SeparationEffectDate),YEAR(@SeparationEffectDate),1,@ContStartDate)
		END
		ELSE
		BEGIN
			SELECT @GrossSalary=GrossSalary,@BasicSalary=BasicSalary
			FROM [dbo].[GetSalaryDetails] (@EmpId,MONTH(@SeparationEffectDate),YEAR(@SeparationEffectDate),1,@ContStartDate)
		END
	END



	--IF(@UnitId in (1,27,45))
	--BEGIN
	--	SELECT @GrossSalary=GrossSalary,@BasicSalary=BasicSalaryOrg
	--	FROM [dbo].[GetSalaryDetails] (@EmpId,@EffectSalaryMonth,@WorkingDayCalculationYear,1,@ContStartDate)
	--END
	--ELSE
	--BEGIN
	--	SELECT @GrossSalary=GrossSalary,@BasicSalary=BasicSalary
	--	FROM [dbo].[GetSalaryDetails] (@EmpId,@EffectSalaryMonth,@WorkingDayCalculationYear,1,@ContStartDate)
	--END


	--//Calculate Salary Stracture Start

	--//Unpaid Salary Calculate Start
	 IF @EmpTypeID=1
	 BEGIN
		SET @ELAmount=(@BasicSalary/30.00)*@ELBalance
	END
	ELSE
	BEGIN
		IF @ELBalance>0
		BEGIN
			SET @ELAmount=(@GrossSalary/30.00)*@ELBalance
		END
		ELSE 
		BEGIN
			SET @ELAmount=(@BasicSalary/30.00)*@ELBalance
		END
	END

	--EXEC ProcessSupplementaryFinalSetelment @EmpID,@SalaryMonth,@SalaryYear,@p_UserId

	--SELECT @UnpaidSalary=GrossPayAmt, @OTRate=OTRate, @grossdays=grossdays, @AllowanceDays=AllowanceDays
	--FROM Payroll_MonthlySalarySupplementary WHERE EmpId=@EmpId AND SalaryMonth=@SalaryMonth AND SalaryYear=@SalaryYear AND Unpaid=1

	SELECT @UnpaidSalary=isnull(NetPayment,0), @OTRate=OTRate, @grossdays=grossdays, @AllowanceDays=AllowanceDays
	FROM Payroll_MonthlySalarySupplementary WHERE EmpId=@EmpId AND SalaryMonth=@SalaryMonth AND SalaryYear=@SalaryYear AND Unpaid=1

--select * from Payroll_MonthlySalarySupplementary	
	--//Unpaid Salary Calculate ENd

	--//Service Length Calculation Star
	SET @LengthYear=(SELECT [dbo].[GetDateDurationYear] (@JoiningDate,@SeparationEffectDate))
	SET @LengthMonth=(SELECT [dbo].[GetDateDurationMonth] (@JoiningDate,@SeparationEffectDate))
	SET @LengthDays=(SELECT [dbo].[GetDateDurationDay] (@JoiningDate,@SeparationEffectDate))
	--//Service Length Calculation ENd


	--//Gratutity Calculation Start
	IF @EmpTypeID=1 AND @EmpCategoryID=1 AND @EmpStatusID <> 2 AND @LengthYear>=5 AND @SeparationEffectDate<'2026-06-28' ---//Updated for Corpore policy provide by taifur vai 
	BEGIN
		IF @LengthYear>=5 AND @LengthMonth>=6 AND @LengthDays>=1
		BEGIN
			SET @LengthYear = @LengthYear+1
		END

		IF @LengthYear BETWEEN 5 AND 9
		BEGIN
			SET @GratuityAmount=(@BasicSalary * (@LengthYear/10.00))*@LengthYear
		END
		IF @LengthYear>9
		BEGIN
			SET @GratuityAmount=@BasicSalary * @LengthYear
		END
	END
	ELSE
	BEGIN
		SET @GratuityAmount=0
	END
	--//Gratutity Calculation End
	--select * from Common_EmploymentStatus where EmpStatusID IN (2,4,13)


	--//Service Benefit Calculation Start

	
	DECLARE @FractionWorkingDay int=(SELECT COUNT (*) FROM Attendance_DailyAttendance WHERE WorkDay BETWEEN DATEADD(YEAR,@LengthYear,@JoiningDate) AND DATEADD(DAY,-1,@SeparationEffectDate) AND EmpID=@EmpId AND AttStatus IN ('P','LT','LTP','SD','TL','EE','NOP','EXL','SL'))

	--IF @FractionWorkingDay>=240 AND @LengthYear >5
	--	BEGIN
	--		SET @LengthYear=@LengthYear+1
	--		SET @LengthMonth=0
	--		SET @LengthDays=0
	--	END

	IF @EmpTypeID IN (2,3,4) AND  @EmpCategoryID=1 AND @EmpStatusID NOT IN (2,4,11,13) AND @LengthYear>=3 AND @SeparationEffectDate<'2026-03-01'
	BEGIN
		
		--Ager Formula
		--IF @LengthYear BETWEEN 5 AND 9
		--BEGIN
		--	IF @LengthMonth>=8 
		--		SET @ServiceBenefitAmount=((@BasicSalary/30.00) * 14) * (@LengthYear+1)
		--	ELSE
		--		SET @ServiceBenefitAmount=((@BasicSalary/30.00) * 14) * @LengthYear
		--END
		--IF @LengthYear>9
		--BEGIN
		--	SET @ServiceBenefitAmount=ROUND(@BasicSalary * @LengthYear,0)
		--END

		---Added by Rakibul Alam for calculating fraction day above or equal 240 ---




		IF @LengthYear BETWEEN 3 AND 9 AND @EmpStatusID  NOT IN (8) --NO Service Benefit for DEATH
		BEGIN

			IF @LengthMonth>=8 
			BEGIN
				IF(@FractionWorkingDay>=240)
				BEGIN

					SET @ServiceBenefitAmount=(((@BasicSalary/30.00) * 15) * (@LengthYear+1))

					
				END
				--ELSE IF @LengthYear=3 AND @FractionWorkingDay<=239
				--BEGIN
				--	SET @ServiceBenefitAmount=((@BasicSalary/30.00) * 7) * @LengthYear
				--END
				BEGIN 
					SET @ServiceBenefitAmount=((@BasicSalary/30.00) * 15) * @LengthYear

				END
			END
			ELSE
			BEGIN
				SET @ServiceBenefitAmount=((@BasicSalary/30.00) * 15) * @LengthYear
			END
		END
		ELSE IF (@LengthYear>=10) -- REF CCL
		BEGIN 
			SET @ServiceBenefitAmount=ROUND((@BasicSalary * @LengthYear),0)
		END

		--IF @EmpStatusID NOT IN (8)
		--BEGIN

		--	IF(@FractionWorkingDay>=240) AND @EmpTypeID IN  (2,3,4)
		--	BEGIN
		--		SET @ServiceBenefitAmount=(((@BasicSalary/30.00) * 14) * (@LengthYear+1))
		--	END
		--	ELSE IF (@FractionWorkingDay>=181) AND @EmpTypeID=1
		--	BEGIN
		--		PRINT 'T'
		--	END
		--END

	END	 
		ELSE IF (@EmpTypeID IN (2,3,4) OR (@EmpTypeID IN (1) AND @SeparationEffectDate>='2026-06-28')) AND  @EmpCategoryID=1 AND @EmpStatusID NOT IN (2,4,11,13) AND @LengthYear>=1 AND @SeparationEffectDate>='2026-03-01'
	BEGIN

		IF @LengthYear BETWEEN 3 AND 9 AND @EmpStatusID  NOT IN (8) --NO Service Benefit for DEATH
		BEGIN

			IF @LengthYear=3 AND (@LengthMonth<1 AND @LengthDays<1)
			BEGIN
					SET @ServiceBenefitAmount=((@BasicSalary/30.00) * 7) * @LengthYear
			END
			ELSE 
			BEGIN
			 	IF @LengthMonth>=6
				BEGIN
					IF(@FractionWorkingDay>=240)
					BEGIN

						SET @ServiceBenefitAmount=(((@BasicSalary/30.00) * 15) * (@LengthYear+1))
					
					END
					ELSE
					BEGIN 
						SET @ServiceBenefitAmount=((@BasicSalary/30.00) * 15) * @LengthYear

					END
				END
				ELSE
				BEGIN
					SET @ServiceBenefitAmount=((@BasicSalary/30.00) * 15) * @LengthYear
				END
			END
			

		END

		ELSE IF (@LengthYear>=10) --Following the Gazzete 
		BEGIN 
		IF @LengthMonth>=6
			BEGIN
				IF(@FractionWorkingDay>=240)
				BEGIN

					SET @ServiceBenefitAmount=ROUND((@BasicSalary * (@LengthYear+1)),0)
					
				END
				ELSE
				BEGIN 
					SET @ServiceBenefitAmount=ROUND((@BasicSalary *@LengthYear),0)

				END
			END
			ELSE
			BEGIN
				SET @ServiceBenefitAmount=ROUND((@BasicSalary * @LengthYear),0)
			END

		END

	END
	ELSE IF @EmpTypeID IN (2,3,4) AND  @EmpCategoryID=1 AND @EmpStatusID IN (11) AND @LengthYear>=1
	BEGIN
		IF @LengthMonth>=6
			BEGIN
				IF(@FractionWorkingDay>=240)
				BEGIN

					SET @ServiceBenefitAmount=ROUND((@BasicSalary * (@LengthYear+1)),0)
					
				END
				ELSE
				BEGIN 
					SET @ServiceBenefitAmount=ROUND((@BasicSalary *@LengthYear),0)

				END
			END
			ELSE
			BEGIN
				SET @ServiceBenefitAmount=ROUND((@BasicSalary * @LengthYear),0)
			END

	END	 
	ELSE IF @EmpTypeID IN (2,3,4) AND  @EmpCategoryID=1 AND @EmpStatusID IN (4,13)
	BEGIN
		SET @ServiceBenefitAmount=ROUND((@BasicSalary * @LengthYear),0)
	END	 
	ELSE
	BEGIN
		SET @ServiceBenefitAmount=0
	END

	IF @EmpTypeID IN (2,3,4) AND  @EmpCategoryID=1 AND @EmpStatusID IN (13)
	BEGIN
		SET @ServiceBenefitAmount=0 --//ADDED BY TAMAL, SUGGESTED BY TAMZID VAI TONGI 
	END
	IF @EmpStatusID = 8 --DEATH
	BEGIN
		SET @ServiceBenefitAmount=0 --//ADDED BY ABIR
	END
	--//Service Benefit Calculation Start

	--//Termination Benefir Start



	IF @EmpTypeID IN (2,3,4) AND  @EmpCategoryID=1 AND @EmpStatusID = 4 AND @LengthYear >= 1
	BEGIN
		--SET @TerminationBenefit=ROUND(ROUND(@BasicSalary,0) * @LengthYear,0)

		SET @TerminationBenefit=ROUND(ROUND(@BasicSalary/30,0) * 120,0)
	END
	ELSE
	BEGIN
		SET @TerminationBenefit=0
	END
	--//Termination Benefit End

	--SELECT 'T',@TerminationBenefit,@LengthYear

	--//Removal Benefit Start
	IF @EmpTypeID IN (2,3,4) AND  @EmpCategoryID=1 AND @EmpStatusID = 13 AND @LengthYear >= 1
	BEGIN
		SET @RemovalBenefit=((@BasicSalary/2) * @LengthYear )
	END
	ELSE
	BEGIN
		SET @RemovalBenefit=0
	END
	--//Removal Benefit End
	
	

	SELECT * FROM Common_EmploymentType



	--//Retirment Benefit Start
	IF @EmpStatusID = 2 AND @EmpCategoryID=1 AND @LengthYear>=1 ---//Updated for Corpore policy provide by taifur vai 
	BEGIN
		IF @EmpTypeID=1  ---For Management ----
		BEGIN
			IF @LengthYear>=5 AND @LengthMonth>=6
			BEGIN
				SET @LengthYear=@LengthYear+1
				SET @LengthMonth=0
				SET @LengthDays=0
			END
			SET @RetirmentBenefitAmount=@BasicSalary * @LengthYear
			END
		ELSE
		BEGIN ---For Non Management ----
			--DECLARE @FractionWorkingDay int=(SELECT COUNT (*) FROM Attendance_DailyAttendance WHERE WorkDay BETWEEN DATEADD(YEAR,@LengthYear,@JoiningDate) AND DATEADD(DAY,-1,@SeparationEffectDate) AND EmpID=@EmpId AND AttStatus IN ('P','LT','LTP','SD','TL','EE','NOP','EXL','ML'))
			IF @FractionWorkingDay>=240
			BEGIN
				SET @LengthYear=@LengthYear+1
				SET @LengthMonth=0
				SET @LengthDays=0
			END
			SET @RetirmentBenefitAmount=@BasicSalary * @LengthYear
		END
	END

	--//Retirment Benefit End

	
--// Death Benefit start  ------
		
		IF @LengthMonth>=6 AND @EmpStatusID=8 AND @LengthYear>IIF(GETDATE()>='2026-01-01',1,2)
		BEGIN
			------When the emplyee die inside the company

			--SET @benefit = (@BasicSalary / 30) * 45; -- from gadget 45 days basic benefit
			--SET @DeathBenefitAmount = (ROUND(@benefit, 0) * (@LengthYear + 1)); 

			-------When the death is normal
			SET @DeathBenefitAmount=ROUND((@BasicSalary * (@LengthYear+1)),0)

			SET @RetirmentBenefitAmount=ROUND((@BasicSalary * @LengthYear),0)
		END
		ELSE IF @EmpStatusID=8
		BEGIN
			------When the emplyee die inside the company

			--SET @benefit = (@BasicSalary / 30) * 45; -- from gadget 45 days basic benefit
			--SET @DeathBenefitAmount = (ROUND(@benefit, 0) * (@LengthYear)); 

			-------When the death is normal
			SET @DeathBenefitAmount=ROUND((@BasicSalary * @LengthYear),0)

			SET @RetirmentBenefitAmount=ROUND((@BasicSalary * @LengthYear),0)
		END

	--// Death Benefit end  ------

	--//Expiration Benefit Start
	IF @EmpTypeID=1 AND @EmpCategoryID=1 AND @EmpStatusID = 8 AND @LengthYear>=5
	BEGIN
		IF @LengthYear BETWEEN 5 AND 9
		BEGIN
			SET @ExpirationBenefitAmount=(@BasicSalary/10) * @LengthYear
		END
		IF @LengthYear>9
		BEGIN
			SET @ExpirationBenefitAmount=@BasicSalary * @LengthYear
		END
	END
	ELSE
	BEGIN
		SET @ExpirationBenefitAmount=0
	END

	--//Expiration Benefit End



	--select * from Common_EmploymentStatus -- 08

		--//Discharge Benefit Start
	IF @EmpStatusID = 10 AND @LengthYear>=1
	BEGIN
		SET @DischargeBenefitAmount=@BasicSalary * @LengthYear
	END
	ELSE
	BEGIN
		SET @DischargeBenefitAmount=0
	END
	
	--//Discharge Benefit End

	--//Other Allowance Start
		SET @OtherAllowance=0
	--//Other Allowance end

	--//Provident Fund Start
	IF @UnitId IN (66)
	BEGIN 
		SET @ProvidentFundAmount=ISNULL((SELECT SUM(ISNULL(PFAmountEmp,0)) FROM Payroll_EmployeePFDeduction WHERE EmpId=@EmpID),0)

		IF @ProvidentFundAmount>0
			IF (SELECT TOP 1 CONVERT(VARCHAR,DeductionYearStart)+'-'+CONVERT(VARCHAR,DeductionMonthStart)+'-01' FROM Payroll_EmployeePF WHERE EmpId=@EmpID AND IsClosed=0 ORDER BY PFMembershipDate DESC)<=DATEADD(YEAR,-2,@SeparationEffectDate)
			BEGIN
				SET @ProvidentFundAmount=@ProvidentFundAmount*2
			END
	END

			
	--//Provident Fund  end

	--//BonusAmount Start
		SET @BonusAmount=0
	--//BonusAmount end

	--//NoticePeriod Start
		--SET @NoticePeriodPayableDays=0
		--SET @NoticePeriodPayableAmount=0

		IF @EmpStatusID=11 AND @LengthYear>=1
		BEGIN
			SET @NoticePeriodPayableDays=@NoticePeriod
			SET @NoticePeriodPayableAmount=(@BasicSalary/30.00)*@NoticePeriodPayableDays

						
		END
		ELSE
		BEGIN
			SET @NoticePeriodPayableDays=0
			SET @NoticePeriodPayableAmount=0
		END


		--IF ( @UnitId IN (81))  -- 
		--BEGIN
		--	SET @NoticePeriodPayableDays=@NoticePeriodDeductionDay
		--	SET @NoticePeriodPayableAmount=(@BasicSalary/30.00)*@NoticePeriodPayableDays
						
		--END
	--//NoticePeriod end

	--//Suspension Start
		SET @SuspensionPayday=0
        SET @SuspensionPayAmount=0
	--//Suspension end

	--//LoanDeduction Start
		SET @LoanDeduction=0

		--SELECT @LoanDeduction=ISNULL(InstallmentAmount,0) FROM Payroll_EmployeeLoanInstallment WHERE LoanId=(SELECT LoanId FROM Payroll_EmployeeLoan  WHERE EmpID=@EmpID AND PaymentStartDate<=@SeparationEffectDate AND IsActive=1)

		---Updated by rakibul alam
		SELECT @LoanDeduction=isnull(sum(InstallmentAmount),0) FROM Payroll_EmployeeLoan A INNER JOIN Payroll_EmployeeLoanInstallment B ON A.LoanId=B.LoanId WHERE A.EmpID=@EmpId 
			AND MONTH(InstallmentDate)=@SalaryMonth AND YEAR(InstallmentDate)=@SalaryYear AND A.IsActive=1 AND B.IsHold=0

	--//LoanDeduction end



	--//NoticePeriodDeduction Start
		SET @NoticePeriodDeductionDay=0
		SET @NoticePeriodDeductionAmount=0
		BEGIN
			IF @NoticePeriod < 0 AND @IsWaive=0
			BEGIN
				SET @NoticePeriodDeductionDay=ABS(@NoticePeriod)
				IF @NoticePeriodDeductionDay>60
				BEGIN
					SET @NoticePeriodDeductionDay=60
				END
				ELSE
				BEGIN
					SET @NoticePeriodDeductionDay=@NoticePeriodDeductionDay
				END
					SET @NoticePeriodDeductionAmount=(@BasicSalary/30.00)*@NoticePeriodDeductionDay
			END
		END		
	--//NoticePeriodDeduction end

	--//OtherDeduction Start
		SET @OtherDeduction=0
	--//OtherDeduction end

	--//TaxAmt Start
		SET @TaxAmt=0
	--//TaxAmt end
	
	--//NetPayable Start
		SET @NetPayableAmount=(isnull(@UnpaidSalary,0)+@ELAmount+@GratuityAmount+@ServiceBenefitAmount+@TerminationBenefit+@RemovalBenefit+@RetirmentBenefitAmount
							+@ExpirationBenefitAmount+@DischargeBenefitAmount+@BonusAmount+@OtherAllowance+@TiffinAllowance+@HolidayAllowance+@NightAllowance
							+@MobileAllowance+@NoticePeriodPayableAmount+@SuspensionPayAmount+@ProvidentFundAmount+@DeathBenefitAmount)-(@LoanDeduction+@NoticePeriodDeductionAmount+@OtherDeduction+@TaxAmt)	

							
	--//NetPayable end
--select @UnpaidSalary,@ELAmount,@GratuityAmount,@ServiceBenefitAmount,@TerminationBenefit,@RetirmentBenefitAmount,@ExpirationBenefitAmount,@BonusAmount,
--@OtherAllowance,@TiffinAllowance,@HolidayAllowance,@NightAllowance,@MobileAllowance,@NoticePeriodPayableAmount,@SuspensionPayAmount, @LoanDeduction,@NoticePeriodDeductionAmount,@OtherDeduction,@TaxAmt
		--//StampFee Start
		IF (@NetPayableAmount)>1000 AND @PaymentModeId=2
		BEGIN
			IF @date>='2022-07-01'
			BEGIN
				IF @PaymentModeId IN (2,3) OR @EmpStatusID IN (4,13)
				BEGIN
					SET	@StampFee=20
					IF (@NetPayableAmount)>50000
					BEGIN
						SET	@StampFee=50				
					END
				END
			END
			ELSE
			BEGIN
				SET	@StampFee=10
			END
		END	

	--//StampFee end
	
	SET @NetPayableAmount=@NetPayableAmount-@StampFee

	--select @WorkingDay,@Allocation
	
	DELETE FROM FinalSattlementPayable WHERE EmpID=@EmpID

	INSERT INTO [dbo].[FinalSattlementPayable]
           ([empid]
           ,[salaryPaidCompany]
		   ,[GrossDays]
		   ,[AllowanceDays]
           ,[GrossSalary]
           ,[BasicSalary]
		   ,[OTRate]
		   ,[UnpaidSalary]
           ,[UnitID]
           ,[DepartmentID]
           ,[SectionID]
           ,[WingID]
           ,[TeamID]
           ,[DesignationID]
           ,[EmpTypeID]
           ,[BankID]
           ,[BankAccount]
           ,[LengthYear]
           ,[LengthMonth]
           ,[LengthDays]
           ,[ELBalance]
           ,[ELAmount]
           ,[GratuityAmount]
           ,[ServiceBenefitAmount]
		   ,[TerminationBenefit]
           ,[RetirmentBenefitAmount]
		   ,[ExpirationBenefitAmount]
		   ,[DischargeBenefitAmount]
           ,[BonusAmount]
           ,[OtherAllowance]
		   ,[ProvidentFundAmount]
           ,[TiffinAllowance]
           ,[HolidayAllowance]
           ,[NightAllowance]
           ,[MobileAllowance]
           ,[NoticePeriodPayableDays]
           ,[NoticePeriodPayableAmount]
           ,[SuspensionPayday]
           ,[SuspensionPayAmount]
           ,[LoanDeduction]
           ,[NoticePeriodDeductionDay]
           ,[NoticePeriodDeductionAmount]
           ,[OtherDeduction]
           ,[TaxAmt]
           ,[StampFee]
           ,[NetPayableAmount]
           ,[Remarks]
		   ,[DeathBenefitAmount]
		   )
     VALUES
           (@EmpID
           ,@PayrollCompanyId
		   ,@GrossDays
           ,@AllowanceDays
           ,@GrossSalary
           ,@BasicSalary
		   ,@OTRate
		   ,@UnpaidSalary
           ,@UnitID
           ,@DepartmentID
           ,@SectionID
           ,@WingID
           ,@TeamID
           ,@DesignationID
           ,@EmpTypeID
           ,@BankID
           ,@BankAccount
           ,@LengthYear
           ,@LengthMonth
           ,@LengthDays
           ,@ELBalance
           ,@ELAmount
           ,@GratuityAmount
		   ,@ServiceBenefitAmount
           ,@TerminationBenefit+@RemovalBenefit
           ,@RetirmentBenefitAmount
		   ,@ExpirationBenefitAmount
		   ,@DischargeBenefitAmount
           ,@BonusAmount
           ,@OtherAllowance
		   ,@ProvidentFundAmount
           ,@TiffinAllowance
           ,@HolidayAllowance
           ,@NightAllowance
           ,@MobileAllowance
           ,@NoticePeriodPayableDays
           ,@NoticePeriodPayableAmount
           ,@SuspensionPayday
           ,@SuspensionPayAmount
           ,@LoanDeduction
           ,@NoticePeriodDeductionDay
           ,@NoticePeriodDeductionAmount
           ,@OtherDeduction
           ,@TaxAmt
           ,@StampFee
           ,@NetPayableAmount
           ,@Remarks
		   ,@DeathBenefitAmount
		   )

		TheEnd:

	--SELECT *  FROM Payroll_MonthlySalarySupplementary WHERE EmpId=@EmpID and SalaryMonth=@SalaryMonth and SalaryYear = @SalaryYear
	--SELECT *  FROM Payroll_MonthlySalary WHERE EmpId=@EmpID and SalaryMonth=@SalaryMonth and SalaryYear = @SalaryYear
	--SELECT GrossSalary,ServiceBenefitAmount,RetirmentBenefitAmount ,DeathBenefitAmount,NetPayableAmount ,* FROM FinalSattlementPayable WHERE EmpID=@EmpID
END



