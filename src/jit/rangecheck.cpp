//
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//

//

#include "jitpch.h"
#include "rangecheck.h"

// Max stack depth (path length) in walking the UD chain.
static const int MAX_SEARCH_DEPTH = 100;

// Max nodes to visit in the UD chain for the current method being compiled.
static const int MAX_VISIT_BUDGET = 8192;

// RangeCheck constructor.
RangeCheck::RangeCheck(Compiler* pCompiler)
    : m_pOverflowMap(nullptr)
    , m_pRangeMap(nullptr)
    , m_fMappedDefs(false)
    , m_pDefTable(nullptr)
    , m_pCompiler(pCompiler)
    , m_nVisitBudget(MAX_VISIT_BUDGET)
{
}

bool RangeCheck::IsOverBudget()
{
    return (m_nVisitBudget <= 0);
}

// Get the range map in which computed ranges are cached.
RangeCheck::RangeMap* RangeCheck::GetRangeMap()
{
    if (m_pRangeMap == nullptr)
    {
        m_pRangeMap = new (m_pCompiler->getAllocator()) RangeMap(m_pCompiler->getAllocator());
    }
    return m_pRangeMap;
}

// Get the overflow map in which computed overflows are cached.
RangeCheck::OverflowMap* RangeCheck::GetOverflowMap()
{
    if (m_pOverflowMap == nullptr)
    {
        m_pOverflowMap = new (m_pCompiler->getAllocator()) OverflowMap(m_pCompiler->getAllocator());
    }
    return m_pOverflowMap;
}

// Get the length of the array vn, if it is new.
int RangeCheck::GetArrLength(ValueNum vn)
{
    ValueNum arrRefVN = m_pCompiler->vnStore->GetArrForLenVn(vn);
    return m_pCompiler->vnStore->GetNewArrSize(arrRefVN);
}

// Check if the computed range is within bounds.
bool RangeCheck::BetweenBounds(Range& range, int lower, GenTreePtr upper)
{
#ifdef DEBUG
    if (m_pCompiler->verbose)
    {
        printf("%s BetweenBounds <%d, ", range.ToString(m_pCompiler->getAllocatorDebugOnly()), lower);
        Compiler::printTreeID(upper);
        printf(">\n");
    }
#endif // DEBUG

    // Get the VN for the upper limit.
    ValueNum uLimitVN = upper->gtVNPair.GetConservative();

#ifdef DEBUG
    JITDUMP("VN%04X upper bound is: ", uLimitVN);
    if (m_pCompiler->verbose)
    {
        m_pCompiler->vnStore->vnDump(m_pCompiler, uLimitVN);
    }
    JITDUMP("\n");
#endif

    ValueNum arrRefVN = ValueNumStore::NoVN;
    int arrSize = 0;

    if (m_pCompiler->vnStore->IsVNConstant(uLimitVN))
    {
        ssize_t  constVal = -1;
        unsigned iconFlags = 0;

        if (m_pCompiler->optIsTreeKnownIntValue(true, upper, &constVal, &iconFlags))
        {
            arrSize = (int)constVal;
        }
    }
    else if (m_pCompiler->vnStore->IsVNArrLen(uLimitVN))
    {
        // Get the array reference from the length.
        arrRefVN = m_pCompiler->vnStore->GetArrForLenVn(uLimitVN);
        // Check if array size can be obtained.
        arrSize = m_pCompiler->vnStore->GetNewArrSize(arrRefVN);
    }
    else
    {
    // If the upper limit is not length, then bail.
        return false;
    }

#ifdef DEBUG
    JITDUMP("Array ref VN");
    if (m_pCompiler->verbose)
    {
        m_pCompiler->vnStore->vnDump(m_pCompiler, arrRefVN);
    }
    JITDUMP("\n");
#endif

    JITDUMP("Array size is: %d\n", arrSize);

    // Upper limit: a.len + ucns (upper limit constant).
    if (range.UpperLimit().IsBinOpArray())
    {
        if (range.UpperLimit().vn != arrRefVN)
        {
            return false;
        }

        int ucns = range.UpperLimit().GetConstant();
        
        // Upper limit: a.Len + [0..n]
        if (ucns >= 0)
        {
            return false;
        }

        // If lower limit is a.len return false.
        if (range.LowerLimit().IsArray())
        {
            return false;
        }
 
        // Since upper limit is bounded by the array, return true if lower bound is good.
        if (range.LowerLimit().IsConstant() && range.LowerLimit().GetConstant() >= 0)
        {
            return true;
        }
        
        // Check if we have the array size allocated by new.
        if (arrSize <= 0)
        {
            return false;
        }

        // At this point,
        // upper limit = a.len + ucns. ucns < 0
        // lower limit = a.len + lcns.
        if (range.LowerLimit().IsBinOpArray())
        {
            int lcns = range.LowerLimit().GetConstant();
            if (lcns >= 0 || -lcns > arrSize)
            {
                return false;
            }
            return (range.LowerLimit().vn == arrRefVN && lcns <= ucns); 
        }
    }
    // If upper limit is constant
    else if (range.UpperLimit().IsConstant())
    {
        if (arrSize <= 0)
        {
            return false;
        }
        int ucns = range.UpperLimit().GetConstant();
        if (ucns >= arrSize)
        {
            return false;
        }
        if (range.LowerLimit().IsConstant())
        {
            int lcns = range.LowerLimit().GetConstant();
            // Make sure lcns < ucns which is already less than arrSize.
            return (lcns >= 0 && lcns <= ucns);
        }
        if (range.LowerLimit().IsBinOpArray())
        {
            int lcns = range.LowerLimit().GetConstant();
            // a.len + lcns, make sure we don't subtract too much from a.len.
            if (lcns >= 0 || -lcns > arrSize)
            {
                return false;
            }
            // Make sure a.len + lcns <= ucns.
            return (range.LowerLimit().vn == arrRefVN && (arrSize + lcns) <= ucns);
        }
    }

    return false;
}

void RangeCheck::OptimizeRangeCheck(BasicBlock* block, GenTreePtr stmt, GenTreePtr treeParent)
{
    // Check if we are dealing with a bounds check node.
    if (treeParent->OperGet() != GT_COMMA)
    {
        return;
    }

    // If we are not looking at array bounds check, bail.
    GenTreePtr tree = treeParent->gtOp.gtOp1;
    if (tree->gtOper != GT_ARR_BOUNDS_CHECK)
    {
        return;
    }

    GenTreeBoundsChk* bndsChk = tree->AsBoundsChk();
    m_pCurBndsChk = bndsChk;
    GenTreePtr treeIndex = bndsChk->gtIndex;

    // Take care of constant index first, like a[2], for example.
    ValueNum idxVn = treeIndex->gtVNPair.GetConservative();
    ValueNum arrLenVn = bndsChk->gtArrLen->gtVNPair.GetConservative();
    int arrSize = 0;

    if (m_pCompiler->vnStore->IsVNConstant(arrLenVn))
    {
        ssize_t  constVal = -1;
        unsigned iconFlags = 0;

        if (m_pCompiler->optIsTreeKnownIntValue(true, bndsChk->gtArrLen, &constVal, &iconFlags))
        {
            arrSize = (int)constVal;
        }
    }
    else
    {
        arrSize = GetArrLength(arrLenVn);
    }

    JITDUMP("ArrSize for lengthVN:%03X = %d\n", arrLenVn, arrSize);
    if (m_pCompiler->vnStore->IsVNConstant(idxVn) && arrSize > 0)
    {
        ssize_t idxVal = -1;
        unsigned iconFlags = 0;
        if (!m_pCompiler->optIsTreeKnownIntValue(true, treeIndex, &idxVal, &iconFlags))
        {
            return;
        }

        JITDUMP("[RangeCheck::OptimizeRangeCheck] Is index %d in <0, arrLenVn VN%X sz:%d>.\n", idxVal, arrLenVn, arrSize);
        if (arrSize > 0 && idxVal < arrSize && idxVal >= 0)
        {
            JITDUMP("Removing range check\n");
            m_pCompiler->optRemoveRangeCheck(treeParent, stmt, true, GTF_ASG, true /* force remove */);
            return;
        }
    }

    GetRangeMap()->RemoveAll();
    GetOverflowMap()->RemoveAll();

    // Get the range for this index.
    SearchPath* path = new (m_pCompiler->getAllocator()) SearchPath(m_pCompiler->getAllocator());

    Range range = GetRange(block, stmt, treeIndex, path, false DEBUGARG(0));

    // If upper or lower limit is found to be unknown (top), or it was found to
    // be unknown because of over budget or a deep search, then return early.
    if (range.UpperLimit().IsUnknown() || range.LowerLimit().IsUnknown())
    {
        // Note: If we had stack depth too deep in the GetRange call, we'd be
        // too deep even in the DoesOverflow call. So return early.
        return;
    }

    if (DoesOverflow(block, stmt, treeIndex, path))
    {
        JITDUMP("Method determined to overflow.\n");
        return;
    }

    JITDUMP("Range value %s\n", range.ToString(m_pCompiler->getAllocatorDebugOnly()));
    path->RemoveAll();
    Widen(block, stmt, treeIndex, path, &range);

    // If upper or lower limit is unknown, then return.
    if (range.UpperLimit().IsUnknown() || range.LowerLimit().IsUnknown())
    {
        return;
    }

    // Is the range between the lower and upper bound values.
    if (BetweenBounds(range, 0, bndsChk->gtArrLen))
    {
        JITDUMP("[RangeCheck::OptimizeRangeCheck] Between bounds\n");
        m_pCompiler->optRemoveRangeCheck(treeParent, stmt, true, GTF_ASG, true /* force remove */);
    }
    return;
}

void RangeCheck::Widen(BasicBlock* block, GenTreePtr stmt, GenTreePtr tree, SearchPath* path, Range* pRange)
{
#ifdef DEBUG
    if (m_pCompiler->verbose)
    {
        printf("[RangeCheck::Widen] BB%02d, \n", block->bbNum);
        Compiler::printTreeID(tree);
        printf("\n");
    }
#endif // DEBUG

    Range& range = *pRange;

    // Try to deduce the lower bound, if it is not known already.
    if (range.LowerLimit().IsDependent() || range.LowerLimit().IsUnknown())
    {
        // To determine the lower bound, ask if the loop increases monotonically.
        bool increasing = IsMonotonicallyIncreasing(tree, path);
        JITDUMP("IsMonotonicallyIncreasing %d", increasing);
        if (increasing)
        {
            GetRangeMap()->RemoveAll();
            *pRange = GetRange(block, stmt, tree, path, true DEBUGARG(0));
        }
    }
}

bool RangeCheck::IsBinOpMonotonicallyIncreasing(GenTreePtr op1, GenTreePtr op2, genTreeOps oper, SearchPath* path)
{
    JITDUMP("[RangeCheck::IsBinOpMonotonicallyIncreasing] %p, %p\n", dspPtr(op1), dspPtr(op2));
    // Check if we have a var + const.
    if (op2->OperGet() == GT_LCL_VAR)
    {
        jitstd::swap(op1, op2);
    }
    if (op1->OperGet() != GT_LCL_VAR)
    {
        JITDUMP("Not monotonic because op1 is not lclVar.\n");
        return false;
    }
    switch (op2->OperGet())
    {
    case GT_LCL_VAR:
        return IsMonotonicallyIncreasing(op1, path) && 
            IsMonotonicallyIncreasing(op2, path);

    case GT_CNS_INT:
        return oper == GT_ADD && op2->AsIntConCommon()->IconValue() >= 0 &&
            IsMonotonicallyIncreasing(op1, path);

    default:
        JITDUMP("Not monotonic because expression is not recognized.\n");
        return false;
    }
}

bool RangeCheck::IsMonotonicallyIncreasing(GenTreePtr expr, SearchPath* path)
{
    JITDUMP("[RangeCheck::IsMonotonicallyIncreasing] %p\n", dspPtr(expr));
    if (path->Lookup(expr))
    {
        return true;
    }

    // Add hashtable entry for expr.
    path->Set(expr, NULL);

    // Remove hashtable entry for expr when we exit the present scope.
    auto code = [&] { path->Remove(expr); };
    jitstd::utility::scoped_code<decltype(code)> finally(code);

    // If the rhs expr is constant, then it is not part of the dependency
    // loop which has to increase monotonically.
    ValueNum vn = expr->gtVNPair.GetConservative();
    if (path->GetCount() > MAX_SEARCH_DEPTH)
    {
        return false;
    }
    else if (m_pCompiler->vnStore->IsVNConstant(vn))
    {
        return true;
    }
    // If the rhs expr is local, then try to find the def of the local.
    else if (expr->IsLocal())
    {
        Location* loc = GetDef(expr);
        if (loc == nullptr)
        {
            return false;
        }
        GenTreePtr asg = loc->parent;
        assert(asg->OperKind() & GTK_ASGOP);
        switch (asg->OperGet())
        {
        case GT_ASG:
            return IsMonotonicallyIncreasing(asg->gtGetOp2(), path);

        case GT_ASG_ADD:
            return IsBinOpMonotonicallyIncreasing(asg->gtGetOp1(), asg->gtGetOp2(), GT_ADD, path);

        default:
            // All other 'asg->OperGet()' kinds, return false
            break;
        }
        JITDUMP("Unknown local definition type\n");
        return false;
    }
    else if (expr->OperGet() == GT_ADD)
    {
        return IsBinOpMonotonicallyIncreasing(expr->gtGetOp1(), expr->gtGetOp2(), GT_ADD, path);
    }
    else if (expr->OperGet() == GT_PHI)
    {
        for (GenTreeArgList* args = expr->gtOp.gtOp1->AsArgList();
                args != nullptr; args = args->Rest())
        {
            // If the arg is already in the path, skip.
            if (path->Lookup(args->Current()))
            {
                continue;
            }
            if (!IsMonotonicallyIncreasing(args->Current(), path))
            {
                JITDUMP("Phi argument not monotonic\n");
                return false;
            }
        }
        return true;
    }
    JITDUMP("Unknown tree type\n");
    return false;
}


UINT64 RangeCheck::HashCode(unsigned lclNum, unsigned ssaNum)
{
    assert(ssaNum != SsaConfig::RESERVED_SSA_NUM);
    return UINT64(lclNum) << 32 | ssaNum;
}

// Get the def location of a given variable.
RangeCheck::Location* RangeCheck::GetDef(unsigned lclNum, unsigned ssaNum)
{
    Location* loc = nullptr;
    if (ssaNum == SsaConfig::RESERVED_SSA_NUM)
    {
        return nullptr;
    }
    if (!m_fMappedDefs)
    {
        MapMethodDefs();
    }
    // No defs.
    if (m_pDefTable == nullptr)
    {
        return nullptr;
    }
    m_pDefTable->Lookup(HashCode(lclNum, ssaNum), &loc);
    return loc;
}

RangeCheck::Location* RangeCheck::GetDef(GenTreePtr tree)
{
    assert(tree->IsLocal());
    unsigned lclNum = tree->AsLclVarCommon()->GetLclNum();
    unsigned ssaNum = tree->AsLclVarCommon()->GetSsaNum();
    return GetDef(lclNum, ssaNum);
}

// Add the def location to the hash table.
void RangeCheck::SetDef(UINT64 hash, Location* loc)
{
    if (m_pDefTable == nullptr)
    {
        m_pDefTable = new (m_pCompiler->getAllocator()) VarToLocMap(m_pCompiler->getAllocator());
    }
#ifdef DEBUG
    Location* loc2;
    if (m_pDefTable->Lookup(hash, &loc2))
    {
        JITDUMP("Already have BB%02d, %08X, %08X for hash => %0I64X", loc2->block->bbNum, dspPtr(loc2->stmt), dspPtr(loc2->tree), hash);
        assert(false);
    }
#endif
    m_pDefTable->Set(hash, loc);
}


// Merge assertions on the edge flowing into the block about a variable.
void RangeCheck::MergeEdgeAssertions(GenTreePtr tree, const ASSERT_VALARG_TP assertions, Range* pRange)
{
    if (BitVecOps::IsEmpty(m_pCompiler->apTraits, assertions))
    {
        return;
    }

    GenTreeLclVarCommon* lcl = (GenTreeLclVarCommon*) tree;
    if (lcl->gtSsaNum == SsaConfig::RESERVED_SSA_NUM)
    {
        return;
    }
    // Walk through the "assertions" to check if the apply.
    BitVecOps::Iter iter(m_pCompiler->apTraits, assertions);
    unsigned index = 0;
    while (iter.NextElem(m_pCompiler->apTraits, &index))
    {
        index++;

        Compiler::AssertionDsc* curAssertion = m_pCompiler->optGetAssertion((Compiler::AssertionIndex)index);

        // Current assertion is about array length.
        if (!curAssertion->IsArrLenArithBound() &&
            !curAssertion->IsArrLenBound() &&
            !curAssertion->IsConstantBound())
        {
            continue;
        }

#ifdef DEBUG
        if (m_pCompiler->verbose)
        {
            m_pCompiler->optPrintAssertion(curAssertion, (Compiler::AssertionIndex)index);
        }
#endif

        assert(m_pCompiler->vnStore->IsVNArrLenArithBound(curAssertion->op1.vn) ||
               m_pCompiler->vnStore->IsVNArrLenBound(curAssertion->op1.vn) ||
               m_pCompiler->vnStore->IsVNConstantBound(curAssertion->op1.vn));
        
        Limit limit(Limit::keUndef);
        genTreeOps cmpOper = GT_NONE;

        // Current assertion is of the form (i < a.len - cns) != 0
        if (curAssertion->IsArrLenArithBound())
        {
            ValueNumStore::ArrLenArithBoundInfo info;

            // Get i, a.len, cns and < as "info."
            m_pCompiler->vnStore->GetArrLenArithBoundInfo(curAssertion->op1.vn, &info);
                       
            if (m_pCompiler->lvaTable[lcl->gtLclNum].GetPerSsaData(lcl->gtSsaNum)->m_vnPair.GetConservative()
                    != info.cmpOp)
            {
                continue;
            }

            switch (info.arrOper)
            {
            case GT_SUB:
            case GT_ADD:
                {
                    // If the operand that operates on the array is not constant, then done.
                    if (!m_pCompiler->vnStore->IsVNConstant(info.arrOp) || m_pCompiler->vnStore->TypeOfVN(info.arrOp) != TYP_INT)
                    {
                        break;
                    }
                    int cons = m_pCompiler->vnStore->ConstantValue<int>(info.arrOp);
                    limit = Limit(Limit::keBinOpArray, info.vnArray, info.arrOper == GT_SUB ? -cons : cons);
                }
            }

            cmpOper = (genTreeOps)info.cmpOper;
        }
        // Current assertion is of the form (i < a.len) != 0
        else if (curAssertion->IsArrLenBound())
        {
            ValueNumStore::ArrLenArithBoundInfo info;
    
            // Get the info as "i", "<" and "a.len"
            m_pCompiler->vnStore->GetArrLenBoundInfo(curAssertion->op1.vn, &info);

            ValueNum lclVn = m_pCompiler->lvaTable[lcl->gtLclNum].GetPerSsaData(lcl->gtSsaNum)->m_vnPair.GetConservative();
            // If we don't have the same variable we are comparing against, bail.
            if (lclVn != info.cmpOp)
            {
                continue;
            }
            limit.type = Limit::keArray;
            limit.vn = info.vnArray;
            cmpOper = (genTreeOps)info.cmpOper;
        }
        // Current assertion is of the form (i < 100) != 0
        else if (curAssertion->IsConstantBound())
        {
            ValueNumStore::ConstantBoundInfo info;            

            // Get the info as "i", "<" and "100"
            m_pCompiler->vnStore->GetConstantBoundInfo(curAssertion->op1.vn, &info);

            ValueNum lclVn = m_pCompiler->lvaTable[lcl->gtLclNum].GetPerSsaData(lcl->gtSsaNum)->m_vnPair.GetConservative();

            // If we don't have the same variable we are comparing against, bail.
            if (lclVn != info.cmpOpVN)
            {
                continue;
            }

            limit = Limit(Limit::keConstant, ValueNumStore::NoVN, info.constVal);
            cmpOper = (genTreeOps)info.cmpOper;
        }
        else
        {
            noway_assert(false);
        }

        if (limit.IsUndef())
        {
            continue;
        }

        // Make sure the assertion is of the form != 0 or == 0.
        if (curAssertion->op2.vn != m_pCompiler->vnStore->VNZeroForType(TYP_INT))
        {
            continue;
        }
#ifdef DEBUG
        if (m_pCompiler->verbose) m_pCompiler->optPrintAssertion(curAssertion, (Compiler::AssertionIndex)index);
#endif

        noway_assert(limit.IsBinOpArray() || limit.IsArray() || limit.IsConstant());

        ValueNum arrLenVN = m_pCurBndsChk->gtArrLen->gtVNPair.GetConservative();
        ValueNum arrRefVN = ValueNumStore::NoVN;

        if (m_pCompiler->vnStore->IsVNArrLen(arrLenVN))
        {
            // Get the array reference from the length.
            arrRefVN = m_pCompiler->vnStore->GetArrForLenVn(arrLenVN);
        }

        // During assertion prop we add assertions of the form:
        // 
        //      (i < a.Length) == 0
        //      (i < a.Length) != 0
        //      (i < 100) == 0
        //      (i < 100) != 0
        //
        // At this point, we have detected that op1.vn is (i < a.Length) or (i < a.Length + cns) or
        // (i < 100) and the op2.vn is 0.
        //
        // Now, let us check if we are == 0 (i.e., op1 assertion is false) or != 0 (op1 assertion
        // is true.),
        //
        // If we have an assertion of the form == 0 (i.e., equals false), then reverse relop.
        // The relop has to be reversed because we have: (i < a.Length) is false which is the same
        // as (i >= a.Length).
        if (curAssertion->assertionKind == Compiler::OAK_EQUAL)
        {
            cmpOper = GenTree::ReverseRelop(cmpOper);
        }

        // Bounds are inclusive, so add -1 for upper bound when "<". But make sure we won't overflow.
        if (cmpOper == GT_LT && !limit.AddConstant(-1))
        {
            continue;
        }
        // Bounds are inclusive, so add +1 for lower bound when ">". But make sure we won't overflow.
        if (cmpOper == GT_GT && !limit.AddConstant(1))
        {
            continue;
        }

        // Doesn't tighten the current bound. So skip.
        if (pRange->uLimit.IsConstant() && limit.vn != arrRefVN)
        {
            continue;
        }

        // Check if the incoming limit from assertions tightens the existing upper limit.
        if ((pRange->uLimit.IsArray() || pRange->uLimit.IsBinOpArray()) && pRange->uLimit.vn == arrRefVN)
        {
            // We have checked the current range's (pRange's) upper limit is either of the form:
            //      a.Length
            //      a.Length + cns
            //      and a == the bndsChkCandidate's arrRef
            //
            // We want to check if the incoming limit tightens the bound, and for that the
            // we need to make sure that incoming limit is also on a.Length or a.Length + cns
            // and not b.Length or some c.Length.

            if (limit.vn != arrRefVN)
            {
                JITDUMP("Array ref did not match cur=$%x, assert=$%x\n", arrRefVN, limit.vn);
                continue;
            }

            int curCns = (pRange->uLimit.IsBinOpArray()) ? pRange->uLimit.cns : 0;
            int limCns = (limit.IsBinOpArray()) ? limit.cns : 0;
            
            // Incoming limit doesn't tighten the existing upper limit.
            if (limCns >= curCns)
            {
                JITDUMP("Bound limit %d doesn't tighten current bound %d\n", limCns, curCns);
                continue;
            }
        }
        else
        {
            // Current range's upper bound is not "a.Length or a.Length + cns" and the
            // incoming limit is not on the same arrRef as the bounds check candidate.
            // So we could skip this assertion. But in cases, of Dependent or Unknown
            // type of upper limit, the incoming assertion still tightens the upper
            // bound to a saner value. So do not skip the assertion.
        }

        // cmpOp (loop index i) cmpOper a.len +/- cns
        switch (cmpOper)
        {
        case GT_LT:
            pRange->uLimit = limit;
            break;

        case GT_GT:
            pRange->lLimit = limit;
            break;

        case GT_GE:
            pRange->lLimit = limit;
            break;

        case GT_LE:
            pRange->uLimit = limit;
            break;

        default:
            // All other 'cmpOper' kinds leave lLimit/uLimit unchanged
            break;
        }
        JITDUMP("The range after edge merging:");
        JITDUMP(pRange->ToString(m_pCompiler->getAllocatorDebugOnly()));
        JITDUMP("\n");
    }
}

// Merge assertions from the pred edges of the block, i.e., check for any assertions about "op's" value numbers for phi arguments.
// If not a phi argument, check if we assertions about local variables.
void RangeCheck::MergeAssertion(BasicBlock* block, GenTreePtr stmt, GenTreePtr op, SearchPath* path, Range* pRange DEBUGARG(int indent))
{
    JITDUMP("Merging assertions from pred edges of BB%02d for op(%p) $%03x\n", block->bbNum, dspPtr(op), op->gtVNPair.GetConservative());
    ASSERT_TP assertions = BitVecOps::UninitVal();

    // If we have a phi arg, we can get to the block from it and use its assertion out.
    if (op->gtOper == GT_PHI_ARG)
    {
        GenTreePhiArg* arg = (GenTreePhiArg*) op;
        BasicBlock* pred = arg->gtPredBB;
        if (pred->bbFallsThrough() && pred->bbNext == block)
        {
            assertions = pred->bbAssertionOut;
            JITDUMP("Merge assertions from pred BB%02d edge: %s\n", pred->bbNum, BitVecOps::ToString(m_pCompiler->apTraits, assertions));
        }
        else if ((pred->bbJumpKind == BBJ_COND || pred->bbJumpKind == BBJ_ALWAYS) && pred->bbJumpDest == block)
        {
            if (m_pCompiler->bbJtrueAssertionOut != NULL)
            {
                assertions = m_pCompiler->bbJtrueAssertionOut[pred->bbNum];
                JITDUMP("Merge assertions from pred BB%02d JTrue edge: %0I64X\n", pred->bbNum, assertions);
            }
        }
    }
    // Get assertions from bbAssertionIn.
    else if (op->IsLocal())
    {
        assertions = block->bbAssertionIn;
    }

    if (!BitVecOps::MayBeUninit(assertions))
    {
        // Perform the merge step to fine tune the range value.
        MergeEdgeAssertions(op, assertions, pRange);
    }
}


// Compute the range for a binary operation.
Range RangeCheck::ComputeRangeForBinOp(BasicBlock* block, GenTreePtr stmt,
        GenTreePtr op1, GenTreePtr op2, genTreeOps oper, SearchPath* path, bool monotonic DEBUGARG(int indent))
{
    Range* op1RangeCached = NULL;
    Range op1Range = Limit(Limit::keUndef);
    bool inPath1 = path->Lookup(op1);
    // Check if the range value is already cached.
    if (!GetRangeMap()->Lookup(op1, &op1RangeCached))
    {
        // If we already have the op in the path, then, just rely on assertions, else
        // find the range.
        if (!inPath1)
        {
            op1Range = GetRange(block, stmt, op1, path, monotonic DEBUGARG(indent));
        }
        else
        {
            op1Range = Range(Limit(Limit::keDependent));
        }
        MergeAssertion(block, stmt, op1, path, &op1Range DEBUGARG(indent + 1));
    }
    else
    {
        op1Range = *op1RangeCached;
    }

    Range* op2RangeCached;
    Range op2Range = Limit(Limit::keUndef);
    bool inPath2 = path->Lookup(op2);
    // Check if the range value is already cached.
    if (!GetRangeMap()->Lookup(op2, &op2RangeCached))
    {
        // If we already have the op in the path, then, just rely on assertions, else
        // find the range.
        if (!inPath2)
        {
            op2Range = GetRange(block, stmt, op2, path, monotonic DEBUGARG(indent));
        }
        else
        {
            op2Range = Range(Limit(Limit::keDependent));
        }
        MergeAssertion(block, stmt, op2, path, &op2Range DEBUGARG(indent + 1));
    }
    else
    {
        op2Range = *op2RangeCached;
    }

    assert(oper == GT_ADD); // For now just GT_ADD.
    Range r = RangeOps::Add(op1Range, op2Range);
    JITDUMP("BinOp add ranges %s %s = %s\n",
            op1Range.ToString(m_pCompiler->getAllocatorDebugOnly()),
            op2Range.ToString(m_pCompiler->getAllocatorDebugOnly()),
            r.ToString(m_pCompiler->getAllocatorDebugOnly()));
    return r;
}

// Compute the range for a local var definition.
Range RangeCheck::ComputeRangeForLocalDef(BasicBlock* block, GenTreePtr stmt, GenTreePtr expr, SearchPath* path, bool monotonic DEBUGARG(int indent))
{
    // Get the program location of the def.
    Location* loc = GetDef(expr);

    // If we can't reach the def, then return unknown range.
    if (loc == nullptr)
    {
        return Range(Limit(Limit::keUnknown));
    }
#ifdef DEBUG
    if (m_pCompiler->verbose)
    {
        JITDUMP("----------------------------------------------------\n");
        m_pCompiler->gtDispTree(loc->stmt);
        JITDUMP("----------------------------------------------------\n");
    }
#endif
    GenTreePtr asg = loc->parent;
    assert(asg->OperKind() & GTK_ASGOP);
    switch (asg->OperGet())
    {
    // If the operator of the definition is assignment, then compute the range of the rhs.
    case GT_ASG:
        {
            Range range = GetRange(loc->block, loc->stmt, asg->gtGetOp2(), path, monotonic DEBUGARG(indent));
            JITDUMP("Merge assertions from BB%02d:%016I64X for assignment about %p\n", block->bbNum, block->bbAssertionIn, dspPtr(asg->gtGetOp1()));
            MergeEdgeAssertions(asg->gtGetOp1(), block->bbAssertionIn, &range);
            JITDUMP("done merging\n");
            return range;
        }

    case GT_ASG_ADD:
        // If the operator of the definition is +=, then compute the range of the operands of +.
        // Note that gtGetOp1 will return op1 to be the lhs; in the formulation of ssa, we have
        // a side table for defs and the lhs of a += is considered to be a use for SSA numbering.
        return ComputeRangeForBinOp(loc->block, loc->stmt,
                asg->gtGetOp1(), asg->gtGetOp2(), GT_ADD, path, monotonic DEBUGARG(indent));

    default:
        // All other 'asg->OperGet()' kinds, return Limit::keUnknown
        break;
    }
    return Range(Limit(Limit::keUnknown));
}

// https://msdn.microsoft.com/en-us/windows/apps/hh285054.aspx
// CLR throws IDS_EE_ARRAY_DIMENSIONS_EXCEEDED if array length is > INT_MAX.
// new byte[INT_MAX]; still throws OutOfMemoryException on my system with 32 GB RAM.
// I believe practical limits are still smaller than this number.
#define ARRLEN_MAX (0x7FFFFFFF)

// Get the limit's maximum possible value, treating array length to be ARRLEN_MAX.
bool RangeCheck::GetLimitMax(Limit& limit, int* pMax)
{
    int& max1 = *pMax;
    switch (limit.type)
    {
    case Limit::keConstant:
        max1 = limit.GetConstant();
        break;

    case Limit::keBinOpArray:
        {
            int tmp = GetArrLength(limit.vn);
            if (tmp <= 0)
            {
                tmp = ARRLEN_MAX;
            }
            if (IntAddOverflows(tmp, limit.GetConstant()))
            {
                return false;
            }
            max1 = tmp + limit.GetConstant();
        }
        break;

    case Limit::keArray:
        {
            int tmp = GetArrLength(limit.vn);
            if (tmp <= 0)
            {
                tmp = ARRLEN_MAX;
            }
            max1 = tmp;
        }
        break;

    case Limit::keSsaVar:
    case Limit::keBinOp:
        if (m_pCompiler->vnStore->IsVNConstant(limit.vn) && m_pCompiler->vnStore->TypeOfVN(limit.vn) == TYP_INT)
        {
           max1 = m_pCompiler->vnStore->ConstantValue<int>(limit.vn);
        }
        else
        {
            return false;
        }
        if (limit.type == Limit::keBinOp)
        {
            if (IntAddOverflows(max1, limit.GetConstant()))
            {
                return false;
            }
            max1 += limit.GetConstant();
        }
        break;

    default:
        return false;
    }
    return true;
}

// Check if the arithmetic overflows.
bool RangeCheck::AddOverflows(Limit& limit1, Limit& limit2)
{
    int max1;
    if (!GetLimitMax(limit1, &max1))
    {
        return true;
    }

    int max2;
    if (!GetLimitMax(limit2, &max2))
    {
        return true;
    }

    return IntAddOverflows(max1, max2);
}

// Does the bin operation overflow.
bool RangeCheck::DoesBinOpOverflow(BasicBlock* block, GenTreePtr stmt, GenTreePtr op1, GenTreePtr op2, SearchPath* path)
{
    if (!path->Lookup(op1) && DoesOverflow(block, stmt, op1, path))
    {
        return true;
    }

    if (!path->Lookup(op2) && DoesOverflow(block, stmt, op2, path))
    {
        return true;
    }

    // Get the cached ranges of op1
    Range* op1Range = nullptr;
    if (!GetRangeMap()->Lookup(op1, &op1Range))
    {
        return true;
    }
    // Get the cached ranges of op2
    Range* op2Range = nullptr;
    if (!GetRangeMap()->Lookup(op2, &op2Range))
    {
        return true;
    }

    // If dependent, check if we can use some assertions.
    if (op1Range->UpperLimit().IsDependent())
    {
        MergeAssertion(block, stmt, op1, path, op1Range DEBUGARG(0));
    }

    // If dependent, check if we can use some assertions.
    if (op2Range->UpperLimit().IsDependent())
    {
        MergeAssertion(block, stmt, op2, path, op2Range DEBUGARG(0));
    }

    JITDUMP("Checking bin op overflow %s %s\n",
            op1Range->ToString(m_pCompiler->getAllocatorDebugOnly()),
            op2Range->ToString(m_pCompiler->getAllocatorDebugOnly()));

    if (!AddOverflows(op1Range->UpperLimit(), op2Range->UpperLimit()))
    {
        return false;
    }
    return true;
}

// Check if the var definition the rhs involves arithmetic that overflows.
bool RangeCheck::DoesVarDefOverflow(BasicBlock* block, GenTreePtr stmt, GenTreePtr expr, SearchPath* path)
{
    // Get the definition.
    Location* loc = GetDef(expr);
    if (loc == nullptr)
    {
        return true;
    }
    // Get the parent node which is an asg.
    GenTreePtr asg = loc->parent;
    assert(asg->OperKind() & GTK_ASGOP);
    switch (asg->OperGet())
    {
    case GT_ASG:
        return DoesOverflow(loc->block, loc->stmt, asg->gtGetOp2(), path);

    case GT_ASG_ADD:
        // For GT_ASG_ADD, op2 is use, op1 is also use since we side table for defs in useasg case.
        return DoesBinOpOverflow(loc->block, loc->stmt, asg->gtGetOp1(), asg->gtGetOp2(), path);

    default:
        // All other 'asg->OperGet()' kinds, conservatively return true
        break;
    }
    return true;
}

bool RangeCheck::DoesPhiOverflow(BasicBlock* block, GenTreePtr stmt, GenTreePtr expr, SearchPath* path)
{
    for (GenTreeArgList* args = expr->gtOp.gtOp1->AsArgList();
            args != nullptr;
            args = args->Rest())
    {
        GenTreePtr arg = args->Current();
        if (path->Lookup(arg))
        {
            continue;
        }
        if (DoesOverflow(block, stmt, args->Current(), path))
        {
            return true;
        }
    }
    return false;
}

bool RangeCheck::DoesOverflow(BasicBlock* block, GenTreePtr stmt, GenTreePtr expr, SearchPath* path)
{
    bool overflows = false;
    if (!GetOverflowMap()->Lookup(expr, &overflows))
    {
        overflows = ComputeDoesOverflow(block, stmt, expr, path);
    }
    return overflows;
}

bool RangeCheck::ComputeDoesOverflow(BasicBlock* block, GenTreePtr stmt, GenTreePtr expr, SearchPath* path)
{
    JITDUMP("Does overflow %p?\n", dspPtr(expr));
    path->Set(expr, block);

    bool overflows = true;

    // Remove hashtable entry for expr when we exit the present scope.
    Range range = Limit(Limit::keUndef);
    ValueNum vn = expr->gtVNPair.GetConservative();
    if (path->GetCount() > MAX_SEARCH_DEPTH)
    {
        overflows = true;
    }
    // If the definition chain resolves to a constant, it doesn't overflow.
    else if (m_pCompiler->vnStore->IsVNConstant(vn))
    {
        overflows = false;
    }
    // Check if the var def has rhs involving arithmetic that overflows.
    else if (expr->IsLocal())
    {
        overflows = DoesVarDefOverflow(block, stmt, expr, path);
    }
    // Check if add overflows.
    else if (expr->OperGet() == GT_ADD)
    {
        overflows = DoesBinOpOverflow(block, stmt, expr->gtGetOp1(), expr->gtGetOp2(), path);
    }
    // Walk through phi arguments to check if phi arguments involve arithmetic that overflows.
    else if (expr->OperGet() == GT_PHI)
    {
        overflows = DoesPhiOverflow(block, stmt, expr, path);
    }
    GetOverflowMap()->Set(expr, overflows);
    path->Remove(expr);
    return overflows;
}

struct Node
{
    Range range;
    Node* next;
    Node()
        : range(Limit(Limit::keUndef)),
          next(NULL) {}
};

// Compute the range recursively by asking for the range of each variable in the dependency chain.
// eg.: c = a + b; ask range of "a" and "b" and add the results.
// If the result cannot be determined i.e., the dependency chain does not terminate in a value,
// but continues to loop, which will happen with phi nodes. We end the looping by calling the
// value as "dependent" (dep).
// If the loop is proven to be "monotonic", then make liberal decisions while merging phi node.
// eg.: merge((0, dep), (dep, dep)) = (0, dep)
Range RangeCheck::ComputeRange(BasicBlock* block, GenTreePtr stmt, GenTreePtr expr, SearchPath* path, bool monotonic DEBUGARG(int indent))
{
    bool newlyAdded = !path->Set(expr, block);
    Range range = Limit(Limit::keUndef);

    ValueNum vn = expr->gtVNPair.GetConservative();
    // If newly added in the current search path, then reduce the budget.
    if (newlyAdded)
    {
        // Assert that we are not re-entrant for a node which has been
        // visited and resolved before and not currently on the search path.
        noway_assert(!GetRangeMap()->Lookup(expr));
        m_nVisitBudget--;
    }
    // Prevent quadratic behavior.
    if (IsOverBudget())
    {
        // Set to unknown, since an Unknown range resolution, will stop further
        // searches. This is because anything that merges with Unknown will
        // yield Unknown. Unknown is lattice top.
        range = Range(Limit(Limit::keUnknown));
        JITDUMP("GetRange not tractable within max node visit budget.\n");
    }
    // Prevent unbounded recursion.
    else if (path->GetCount() > MAX_SEARCH_DEPTH)
    {
        // Unknown is lattice top, anything that merges with Unknown will yield Unknown.
        range = Range(Limit(Limit::keUnknown));
        JITDUMP("GetRange not tractable within max stack depth.\n");
    }
    // TODO-CQ: The current implementation is reliant on integer storage types
    // for constants. It could use INT64. Still, representing ULONG constants 
    // might require preserving the var_type whether it is a un/signed 64-bit.
    // JIT64 doesn't do anything for "long" either. No asm diffs.
    else if (expr->TypeGet() == TYP_LONG || expr->TypeGet() == TYP_ULONG)
    {
        range = Range(Limit(Limit::keUnknown));
        JITDUMP("GetRange long or ulong, setting to unknown value.\n");
    }
    // If VN is constant return range as constant.
    else if (m_pCompiler->vnStore->IsVNConstant(vn))
    {
        range = (m_pCompiler->vnStore->TypeOfVN(vn) == TYP_INT)
              ? Range(Limit(Limit::keConstant, m_pCompiler->vnStore->ConstantValue<int>(vn)))
              : Limit(Limit::keUnknown);
    }
    // If local, find the definition from the def map and evaluate the range for rhs.
    else if (expr->IsLocal())
    {
        range = ComputeRangeForLocalDef(block, stmt, expr, path, monotonic DEBUGARG(indent + 1));
        MergeAssertion(block, stmt, expr, path, &range DEBUGARG(indent + 1));
    }
    // If add, then compute the range for the operands and add them.
    else if (expr->OperGet() == GT_ADD)
    {
        range = ComputeRangeForBinOp(block, stmt,
                    expr->gtGetOp1(), expr->gtGetOp2(), GT_ADD, path, monotonic DEBUGARG(indent + 1));
    }
    // If phi, then compute the range for arguments, calling the result "dependent" when looping begins.
    else if (expr->OperGet() == GT_PHI)
    {
        Node* cur = nullptr;
        Node* head = nullptr;
        for (GenTreeArgList* args = expr->gtOp.gtOp1->AsArgList();
                args != nullptr; args = args->Rest())
        {
            // Collect the range for each phi argument in a linked list.
            Node* node = new (m_pCompiler->getAllocator()) Node();
            if (cur != nullptr)
            {
                cur->next = node;
                cur = cur->next;
            }
            else
            {
                head = node;
                cur = head;
            }
            if (path->Lookup(args->Current()))
            {
                JITDUMP("PhiArg %p is already being computed\n", dspPtr(args->Current()));
                cur->range = Range(Limit(Limit::keDependent));
                MergeAssertion(block, stmt, args->Current(), path, &cur->range DEBUGARG(indent + 1));
                continue;
            }
            cur->range = GetRange(block, stmt, args->Current(), path, monotonic DEBUGARG(indent + 1));
            MergeAssertion(block, stmt, args->Current(), path, &cur->range DEBUGARG(indent + 1));
        }
        // Walk the linked list and merge the ranges.
        for (cur = head; cur; cur = cur->next)
        {
            assert(!cur->range.LowerLimit().IsUndef());
            assert(!cur->range.UpperLimit().IsUndef());
            JITDUMP("Merging ranges %s %s:",
                    range.ToString(m_pCompiler->getAllocatorDebugOnly()),
                    cur->range.ToString(m_pCompiler->getAllocatorDebugOnly()));
            range = RangeOps::Merge(range, cur->range, monotonic);
            JITDUMP("%s\n", range.ToString(m_pCompiler->getAllocatorDebugOnly()));
        }
    }
    else
    {
        // The expression is not recognized, so the result is unknown.
        range = Range(Limit(Limit::keUnknown));
    }

    GetRangeMap()->Set(expr, new (m_pCompiler->getAllocator()) Range(range));
    path->Remove(expr);
    return range;
}

#ifdef DEBUG
void Indent(int indent)
{
    for (int i = 0; i < indent; ++i)
    {
        JITDUMP("   ");
    }
}
#endif

// Get the range, if it is already computed, use the cached range value, else compute it.
Range RangeCheck::GetRange(BasicBlock* block, GenTreePtr stmt, GenTreePtr expr, SearchPath* path, bool monotonic DEBUGARG(int indent))
{
#ifdef DEBUG
    if (m_pCompiler->verbose)
    {
        Indent(indent);
        JITDUMP("[RangeCheck::GetRange] BB%02d", block->bbNum);
        m_pCompiler->gtDispTree(expr);
        Indent(indent);
        JITDUMP("{\n", expr);
    }
#endif

    Range* pRange = nullptr;
    Range range = GetRangeMap()->Lookup(expr, &pRange)
                ? *pRange
                : ComputeRange(block, stmt, expr, path, monotonic DEBUGARG(indent));

#ifdef DEBUG
    if (m_pCompiler->verbose)
    {
        Indent(indent);
        JITDUMP("   %s Range (%08X) => %s\n",
            (pRange == nullptr) ? "Computed" : "Cached",
            dspPtr(expr),
            range.ToString(m_pCompiler->getAllocatorDebugOnly()));
        Indent(indent);
        JITDUMP("}\n", expr);
    }
#endif
    return range;
}

// If this is a tree local definition add its location to the def map.
void RangeCheck::MapStmtDefs(const Location& loc)
{
    GenTreePtr tree = loc.tree;
    if (!tree->IsLocal())
    {
        return;
    }

    unsigned lclNum = tree->AsLclVarCommon()->GetLclNum();
    unsigned ssaNum = tree->AsLclVarCommon()->GetSsaNum();
    if (ssaNum == SsaConfig::RESERVED_SSA_NUM)
    {
        return;
    }

    // If useasg then get the correct ssaNum to add to the map.
    if (tree->gtFlags & GTF_VAR_USEASG)
    {
        unsigned ssaNum = m_pCompiler->GetSsaNumForLocalVarDef(tree);
        if (ssaNum != SsaConfig::RESERVED_SSA_NUM)
        {
            // To avoid ind(addr) use asgs
            if (loc.parent->OperKind() & GTK_ASGOP)
            {
                SetDef(HashCode(lclNum, ssaNum),
                    new (m_pCompiler->getAllocator()) Location(loc));
            }
        }
    }
    // If def get the location and store it against the variable's ssaNum.
    else if (tree->gtFlags & GTF_VAR_DEF)
    {
        if (loc.parent->OperGet() == GT_ASG)
        {
            SetDef(HashCode(lclNum, ssaNum), new (m_pCompiler->getAllocator()) Location(loc));
        }
    }
}

struct MapMethodDefsData
{
    RangeCheck* rc;
    BasicBlock* block;
    GenTreePtr stmt;

    MapMethodDefsData(RangeCheck* rc, BasicBlock* block, GenTreePtr stmt)
        : rc(rc)
        , block(block)
        , stmt(stmt)
    { }
};

Compiler::fgWalkResult MapMethodDefsVisitor(GenTreePtr* ptr, Compiler::fgWalkData* data)
{
    MapMethodDefsData* rcd = ((MapMethodDefsData*) data->pCallbackData);
    rcd->rc->MapStmtDefs(RangeCheck::Location(rcd->block, rcd->stmt, *ptr, data->parent));
    return Compiler::WALK_CONTINUE;
}

void RangeCheck::MapMethodDefs()
{
    // First, gather where all definitions occur in the program and store it in a map.
    for (BasicBlock* block = m_pCompiler->fgFirstBB; block; block = block->bbNext)
    {
        for (GenTreePtr stmt = block->bbTreeList; stmt; stmt = stmt->gtNext)
        {
            MapMethodDefsData data(this, block, stmt);
            m_pCompiler->fgWalkTreePre(&stmt->gtStmt.gtStmtExpr, MapMethodDefsVisitor, &data, false, true);
        }
    }
    m_fMappedDefs = true;
}

// Entry point to range check optimizations.
void RangeCheck::OptimizeRangeChecks()
{
    if (m_pCompiler->fgSsaPassesCompleted == 0)
    {
        return;
    }
#ifdef DEBUG
    if  (m_pCompiler->verbose) 
    {
        JITDUMP("*************** In OptimizeRangeChecks()\n");
        JITDUMP("Blocks/trees before phase\n");
        m_pCompiler->fgDispBasicBlocks(true);
    }
#endif

    // Walk through trees looking for arrBndsChk node and check if it can be optimized.
    for (BasicBlock* block = m_pCompiler->fgFirstBB; block; block = block->bbNext)
    {
        for (GenTreePtr stmt = block->bbTreeList; stmt; stmt = stmt->gtNext)
        {
            for (GenTreePtr tree = stmt->gtStmt.gtStmtList; tree; tree = tree->gtNext)
            {
                if (IsOverBudget())
                {
                    return;
                }
                OptimizeRangeCheck(block, stmt, tree);
            }
        }
    }
}
